# Device Selection — Microphone Picker + Remote-App Availability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give LocalScribe a real microphone picker — a persistent pin in Settings and a per-session override in the Record console — that capture actually honors, plus make the remote-app picker available in Auto (not just Per-process) so an explicitly chosen app is captured per-app for the session.

**Architecture:** Enumeration and the pinned-mic decision live WPF-free in `Core`: a thin `WasapiCaptureDeviceEnumerator` (over the already-referenced NAudio) lists active capture endpoints, and a PURE `MicCapturePlanner.Plan` decides — mirroring the existing `RemoteCapturePlanner` — whether to open a pinned device by `Id`, fall back to the Communications default, and whether that fall-back happened. `WasapiCaptureSourceProvider.CreateMic` applies the plan (opens the device, builds the honest `MicSnapshot`); `SessionController` writes the spec §12 `pinned microphone unavailable → default` marker when the plan fell back. The pickers are App view-models over an injected `ICaptureDeviceEnumerator`; a new `MicOverride` service (twin of `RemoteAppOverride`) layers the console's per-session choice over the live `Func<Settings>` seam, and `RemoteAppOverride.Apply` is widened so a chosen app forces per-process capture from any base mode.

**Tech Stack:** C# / .NET 10, NAudio 2.2.1 (`MMDeviceEnumerator`), WPF + WPF-UI (FluentWindow), CommunityToolkit.Mvvm, xUnit. Core is WPF-free and headless-testable.

## Global Constraints

- **Never silently rebind a pin.** A pinned mic that is absent at Start falls back to the Windows Communications default AND emits the `pinned microphone unavailable → default` transcript marker (`Markers.PinnedMicUnavailable`, spec §12). The evidentiary record must show the fall-back happened.
- **Honest snapshot.** `SessionRecord.Devices.Mic` (`MicSnapshot`) records the device actually captured (`Mode`, `Id`, `Name`) plus whether a fall-back occurred (`FellBackToDefault`) — never the intended config.
- **Core stays WPF-free.** Enumeration + the pin decision live in `Core`; the pickers are App VMs over an injected `ICaptureDeviceEnumerator`.
- **Real capture is a hardware seam.** Opening a device by `Id` (`MMDeviceEnumerator().GetDevice`) and the default endpoint cannot be unit-tested headlessly — the thin enumerator and `CreateMic` are smoke-verified, exactly like `WasapiSessionScanner`/`CreateRemote` today. The *decision* logic (`MicCapturePlanner`) and the *marker* wiring (`SessionController` over `FakeProvider`) ARE unit-tested. VM tests inject a `FakeCaptureDeviceEnumerator`.
- **Settings auto-save on field commit** (no Save button) via `ISettingsService.SaveAsync` through the existing `SettingsPageViewModel.Commit → CommitAsync` chain. Capture pulls fresh settings at every Start through the composed `current()` (`CompositionRoot.cs:62`).
- **Per-session overrides never persist.** The console's mic/app choices live in `MicOverride`/`RemoteAppOverride` and revert on Idle — never written to `settings.json`.
- **No settings.json schema change.** `mic { mode, id, name }` and `remote { mode, app }` already exist (schema v3, `Settings.cs:42-43`). `MicSnapshot.FellBackToDefault` is an additive bool (defaults `false`), the same additive pattern as `SectionGapMs`/`RemoteSnapshot.FellBackToSystemMix` — no schema bump, no migration.
- **WPF-free Core:** everything under `src/LocalScribe.Core` has no WPF references.
- **No Unicode emojis in test scripts** (user rule).
- **Zero-warning build gate:** `dotnet build LocalScribe.slnx -c Debug --nologo` must stay 0 warnings / 0 errors. App test suite green; Core suite has 2 KNOWN pre-existing fixture fails (`Der_within_baseline_plus_epsilon`, `Golden_pair_wer_stays_at_baseline`) — the bar is "no NEW failures".

**Build/test commands** (run from `F:\LocalScribe`; close a running `LocalScribe.App.exe` FIRST — it locks `Core.dll`/the app exe and causes MSB3027 copy errors that are NOT compile failures):
- Full build gate: `dotnet build LocalScribe.slnx -c Debug --nologo`
- Core tests: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj`
- App tests: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj`
- Single test class: append ` --filter "FullyQualifiedName~ClassName"`

## File Structure

**Core (new):**
- `src/LocalScribe.Core/Live/CaptureDeviceEnumerator.cs` — `AudioDeviceInfo`, `ICaptureDeviceEnumerator`, `WasapiCaptureDeviceEnumerator` (Task 1).
- `src/LocalScribe.Core/Live/MicCapturePlanner.cs` — `MicPlan`, `MicCapturePlanner.Plan` (Task 2).

**Core (modified):**
- `src/LocalScribe.Core/Model/SessionRecord.cs` — `MicSnapshot.FellBackToDefault` (Task 3).
- `src/LocalScribe.Core/Audio/MicCaptureSource.cs` — by-`Id` ctor + `DeviceId` (Task 4).
- `src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs` — `CreateMic` honors the pin (Task 4).
- `src/LocalScribe.Core/Live/SessionController.cs` — pin-unavailable marker (Task 5).

**App (new):**
- `src/LocalScribe.App/Services/MicOverride.cs` — per-session mic override (Task 6).

**App (modified):**
- `src/LocalScribe.App/Services/RemoteAppOverride.cs` — chosen app forces per-process (Task 7).
- `src/LocalScribe.App/CompositionRoot.cs` — enumerator + `MicOverride` wiring (Task 8).
- `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs` + `src/LocalScribe.App/SettingsPage.xaml` — Settings pin picker (Tasks 9–10).
- `src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs` + `src/LocalScribe.App/LiveViewWindow.xaml` — console app availability + mic override (Tasks 11–13).
- `src/LocalScribe.App/App.xaml.cs` — pass the injected enumerator / `MicOverride` to both VMs (Tasks 9, 12).

**Test double (new, shared by Core + App tests):**
- `tests/LocalScribe.Core.Tests/FakeCaptureDeviceEnumerator.cs` (Task 1). App tests already `using LocalScribe.Core.Tests;`, so one copy serves both suites.

**Docs:**
- `docs/specs/localscribe-specs.md` — §12 deltas (Task 14).
- `docs/plans/2026-07-08-device-selection-smoke-runbook.md` — hardware + GUI smoke (Task 15).

---

## Phase 1 — Core: enumeration, decision, capture, marker

### Task 1: `AudioDeviceInfo` + `ICaptureDeviceEnumerator` + `WasapiCaptureDeviceEnumerator`

**Files:**
- Create: `src/LocalScribe.Core/Live/CaptureDeviceEnumerator.cs`
- Create: `tests/LocalScribe.Core.Tests/FakeCaptureDeviceEnumerator.cs`
- Test: `tests/LocalScribe.Core.Tests/CaptureDeviceEnumeratorTests.cs` (create)

**Interfaces:**
- Produces: `AudioDeviceInfo(string Id, string Name)`; `interface ICaptureDeviceEnumerator { IReadOnlyList<AudioDeviceInfo> ListInputDevices(); }`; `WasapiCaptureDeviceEnumerator : ICaptureDeviceEnumerator` (thin over NAudio, returns `[]` on any enumeration exception); and a test-project `FakeCaptureDeviceEnumerator` seeded with a fixed device list (or set to throw → the production impl's empty-list contract is what VM tests rely on).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/CaptureDeviceEnumeratorTests.cs
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.Core.Tests;

public class CaptureDeviceEnumeratorTests
{
    [Fact]
    public void AudioDeviceInfo_CarriesIdAndName()
    {
        var d = new AudioDeviceInfo("{0.0.1.00000000}.{guid}", "Headset Microphone");
        Assert.Equal("{0.0.1.00000000}.{guid}", d.Id);
        Assert.Equal("Headset Microphone", d.Name);
    }

    [Fact]
    public void FakeEnumerator_ReturnsSeededDevices()
    {
        var fake = new FakeCaptureDeviceEnumerator(
            new AudioDeviceInfo("id-1", "Mic One"),
            new AudioDeviceInfo("id-2", "Mic Two"));
        var list = fake.ListInputDevices();
        Assert.Equal(2, list.Count);
        Assert.Equal("Mic Two", list[1].Name);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~CaptureDeviceEnumeratorTests"`
Expected: FAIL — `AudioDeviceInfo`/`ICaptureDeviceEnumerator`/`FakeCaptureDeviceEnumerator` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.Core/Live/CaptureDeviceEnumerator.cs
using NAudio.CoreAudioApi;
namespace LocalScribe.Core.Live;

/// <summary>An active input (capture) endpoint: the WASAPI device Id (stable across sessions,
/// what a pin stores) plus the friendly name for display. Design section 1.</summary>
public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>Lists active capture endpoints for the mic pickers. Faked in VM tests; the real
/// implementation is thin over NAudio and smoke-verified (like WasapiSessionScanner).</summary>
public interface ICaptureDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> ListInputDevices();
}

/// <summary>Thin adapter (Humble Object) over NAudio: enumerates ACTIVE capture endpoints and
/// projects each to AudioDeviceInfo(d.ID, d.FriendlyName). Any enumeration failure returns an
/// EMPTY list (design section 1/7): the picker then offers only "follow default" and capture uses
/// the Communications default - it never crashes the Settings page or console. Exercised live by
/// the hardware smoke, not unit tests.</summary>
public sealed class WasapiCaptureDeviceEnumerator : ICaptureDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> ListInputDevices()
    {
        try
        {
            var result = new List<AudioDeviceInfo>();
            foreach (var d in new MMDeviceEnumerator()
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                result.Add(new AudioDeviceInfo(d.ID, d.FriendlyName));
            return result;
        }
        catch
        {
            return [];   // no devices / enumeration failed -> follow-default only
        }
    }
}
```

```csharp
// tests/LocalScribe.Core.Tests/FakeCaptureDeviceEnumerator.cs
using LocalScribe.Core.Live;

namespace LocalScribe.Core.Tests;

/// <summary>Deterministic ICaptureDeviceEnumerator for planner + VM tests. Seed a fixed device
/// list, or set Throws=true to simulate an enumeration failure (the production enumerator swallows
/// that into an empty list; a VM under test can assert the empty-list path directly by seeding no
/// devices). Shared by Core.Tests and App.Tests (App.Tests references this namespace).</summary>
public sealed class FakeCaptureDeviceEnumerator : ICaptureDeviceEnumerator
{
    private readonly IReadOnlyList<AudioDeviceInfo> _devices;
    public FakeCaptureDeviceEnumerator(params AudioDeviceInfo[] devices) => _devices = devices;
    public IReadOnlyList<AudioDeviceInfo> ListInputDevices() => _devices;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~CaptureDeviceEnumeratorTests"`
Expected: PASS (both facts).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Live/CaptureDeviceEnumerator.cs tests/LocalScribe.Core.Tests/FakeCaptureDeviceEnumerator.cs tests/LocalScribe.Core.Tests/CaptureDeviceEnumeratorTests.cs
git commit -m "feat(core): capture-device enumerator (interface + NAudio impl + fake)"
```

---

### Task 2: `MicCapturePlanner.Plan` — the pure pin decision

**Files:**
- Create: `src/LocalScribe.Core/Live/MicCapturePlanner.cs`
- Test: `tests/LocalScribe.Core.Tests/MicCapturePlannerTests.cs` (create)

**Interfaces:**
- Consumes: `AudioDeviceInfo` (Task 1); `MicSetting`, `MicMode` (`Model/Settings.cs`, `Model/Enums.cs`).
- Produces: `MicPlan(MicMode Mode, string? DeviceId, bool FellBackToDefault)` and `static MicCapturePlanner.Plan(MicSetting mic, IReadOnlyList<AudioDeviceInfo> devices) → MicPlan`. Pinned + `Id` present in `devices` → `Pinned` with that `DeviceId`. Pinned + `Id` absent (or null/empty) → `FollowDefault` with `FellBackToDefault: true`. `FollowDefault` mode → `FollowDefault`, no fall-back. This is the fully unit-testable twin of `RemoteCapturePlanner.Plan`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/MicCapturePlannerTests.cs
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.Core.Tests;

public class MicCapturePlannerTests
{
    private static readonly AudioDeviceInfo[] TwoMics =
    [
        new("id-headset", "Headset Microphone"),
        new("id-webcam", "Webcam Mic"),
    ];

    [Fact]
    public void PinnedPresent_OpensById_NoFallback()
    {
        var mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-headset", Name = "Headset Microphone" };
        var plan = MicCapturePlanner.Plan(mic, TwoMics);
        Assert.Equal(MicMode.Pinned, plan.Mode);
        Assert.Equal("id-headset", plan.DeviceId);
        Assert.False(plan.FellBackToDefault);
    }

    [Fact]
    public void PinnedAbsent_FallsBackToDefault_AndFlagsIt()
    {
        var mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-unplugged", Name = "Old USB Mic" };
        var plan = MicCapturePlanner.Plan(mic, TwoMics);
        Assert.Equal(MicMode.FollowDefault, plan.Mode);
        Assert.Null(plan.DeviceId);
        Assert.True(plan.FellBackToDefault);
    }

    [Fact]
    public void PinnedWithNullId_IsTreatedAsFollowDefault_NoFallbackMarker()
    {
        // A malformed pin (mode pinned but no id) is not an "unavailable device"; it is just
        // follow-default. No marker (nothing was pinned to be unavailable).
        var mic = new MicSetting { Mode = MicMode.Pinned, Id = null, Name = null };
        var plan = MicCapturePlanner.Plan(mic, TwoMics);
        Assert.Equal(MicMode.FollowDefault, plan.Mode);
        Assert.False(plan.FellBackToDefault);
    }

    [Fact]
    public void FollowDefault_IsUnchanged()
    {
        var plan = MicCapturePlanner.Plan(new MicSetting(), TwoMics);
        Assert.Equal(MicMode.FollowDefault, plan.Mode);
        Assert.Null(plan.DeviceId);
        Assert.False(plan.FellBackToDefault);
    }

    [Fact]
    public void PinnedPresent_ButEmptyDeviceList_FallsBack()
    {
        var mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-headset", Name = "Headset Microphone" };
        var plan = MicCapturePlanner.Plan(mic, []);   // enumeration returned nothing
        Assert.Equal(MicMode.FollowDefault, plan.Mode);
        Assert.True(plan.FellBackToDefault);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~MicCapturePlannerTests"`
Expected: FAIL — `MicPlan`/`MicCapturePlanner` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.Core/Live/MicCapturePlanner.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Live;

/// <summary>The resolved mic capture decision (design section 2). Mode is the HONEST mode the
/// snapshot will record: Pinned only when the pinned device is actually present; otherwise
/// FollowDefault. FellBackToDefault is true only when a real pin was requested but its device is
/// absent - that drives the spec section 12 "pinned microphone unavailable -> default" marker.</summary>
public sealed record MicPlan(MicMode Mode, string? DeviceId, bool FellBackToDefault);

/// <summary>Pure pin resolution (design section 2), the mic twin of RemoteCapturePlanner. Given
/// the saved MicSetting and the live capture-device list, decides whether to open a device by Id,
/// fall back to the Communications default, and whether that fall-back happened. No hardware, no
/// NAudio - fully unit-tested; WasapiCaptureSourceProvider.CreateMic applies the result.</summary>
public static class MicCapturePlanner
{
    public static MicPlan Plan(MicSetting mic, IReadOnlyList<AudioDeviceInfo> devices)
    {
        if (mic.Mode == MicMode.Pinned && !string.IsNullOrEmpty(mic.Id))
        {
            bool present = devices.Any(d => d.Id == mic.Id);
            return present
                ? new MicPlan(MicMode.Pinned, mic.Id, FellBackToDefault: false)
                : new MicPlan(MicMode.FollowDefault, null, FellBackToDefault: true);
        }
        return new MicPlan(MicMode.FollowDefault, null, FellBackToDefault: false);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~MicCapturePlannerTests"`
Expected: PASS (all five facts).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Live/MicCapturePlanner.cs tests/LocalScribe.Core.Tests/MicCapturePlannerTests.cs
git commit -m "feat(core): pure MicCapturePlanner for the pinned-mic decision"
```

---

### Task 3: `MicSnapshot.FellBackToDefault` — the honest fall-back flag

**Files:**
- Modify: `src/LocalScribe.Core/Model/SessionRecord.cs:41-46`
- Test: `tests/LocalScribe.Core.Tests/MicSnapshotTests.cs` (create)

**Interfaces:**
- Produces: additive `bool FellBackToDefault { get; init; }` on `MicSnapshot` (default `false`). Mirrors `RemoteSnapshot.FellBackToSystemMix`; additive to session.json (no schema bump).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/MicSnapshotTests.cs
using System.Text.Json;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.Core.Tests;

public class MicSnapshotTests
{
    [Fact]
    public void FellBackToDefault_DefaultsFalse_AndRoundTrips()
    {
        Assert.False(new MicSnapshot().FellBackToDefault);

        var snap = new MicSnapshot
        { Mode = MicMode.FollowDefault, Name = "Default Mic", FellBackToDefault = true };
        string json = JsonSerializer.Serialize(snap, LocalScribeJson.Options);
        var back = JsonSerializer.Deserialize<MicSnapshot>(json, LocalScribeJson.Options)!;
        Assert.True(back.FellBackToDefault);
        Assert.Equal("Default Mic", back.Name);
    }

    [Fact]
    public void PreExistingJson_WithoutFlag_LoadsAsFalse()
    {
        // A v3 session.json written before this field existed must still load (additive field).
        const string legacy = """{"mode":"pinned","id":"id-1","name":"Studio Mic"}""";
        var back = JsonSerializer.Deserialize<MicSnapshot>(legacy, LocalScribeJson.Options)!;
        Assert.Equal(MicMode.Pinned, back.Mode);
        Assert.False(back.FellBackToDefault);
    }
}
```

> `LocalScribeJson.Options` is the shared serializer options used across the model tests. If the type is named differently in `src/LocalScribe.Core/Storage/`, match the existing usage (grep an existing model round-trip test, e.g. `SettingsTests`, for the exact options accessor).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~MicSnapshotTests"`
Expected: FAIL — `MicSnapshot` has no `FellBackToDefault`.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.Core/Model/SessionRecord.cs — add the field to MicSnapshot
public sealed record MicSnapshot
{
    public MicMode Mode { get; init; } = MicMode.FollowDefault;
    public string? Id { get; init; }
    public string? Name { get; init; }
    /// <summary>True when Mode was Pinned but the pinned device was absent at Start, so capture
    /// fell back to the Communications default (design section 2). Drives the spec section 12
    /// "pinned microphone unavailable -> default" marker. Additive (defaults false): pre-existing
    /// v3 session.json files load unchanged - no schema bump. Mirrors
    /// RemoteSnapshot.FellBackToSystemMix.</summary>
    public bool FellBackToDefault { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~MicSnapshotTests"`
Expected: PASS (both facts).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Model/SessionRecord.cs tests/LocalScribe.Core.Tests/MicSnapshotTests.cs
git commit -m "feat(core): MicSnapshot.FellBackToDefault (additive honest fall-back flag)"
```

---

### Task 4: Capture honors the pin — `MicCaptureSource` by-Id + `CreateMic`

**Files:**
- Modify: `src/LocalScribe.Core/Audio/MicCaptureSource.cs:25-44`
- Modify: `src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs`
- Test: none new (hardware seam — see below). Gate is the build + the unchanged Core suite; the *decision* is covered by Task 2 and the *marker* by Task 5.

**Interfaces:**
- Consumes: `ICaptureDeviceEnumerator`/`AudioDeviceInfo` (Task 1); `MicCapturePlanner`/`MicPlan` (Task 2); `MicSnapshot.FellBackToDefault` (Task 3).
- Produces:
  - `MicCaptureSource` gains `public string DeviceId { get; }` and a by-Id constructor `MicCaptureSource(IClock clock, string deviceId)` (opens `MMDeviceEnumerator().GetDevice(deviceId)`); the default-endpoint ctor is unchanged in behavior.
  - `WasapiCaptureSourceProvider` primary ctor gains an optional `ICaptureDeviceEnumerator? deviceEnumerator = null` (defaults to a real `WasapiCaptureDeviceEnumerator()`), so existing 2-arg call sites (`CompositionRoot.cs:67`, `LiveRunner Program.cs:54`) still compile untouched. `CreateMic` reads `settings.Mic`, runs `MicCapturePlanner.Plan`, opens the device (by Id or default), and returns the honest `MicSnapshot` incl. `FellBackToDefault`.

> **Why no unit test here:** both the by-Id ctor and `CreateMic` construct a real `MicCaptureSource`, which opens a WASAPI device in its constructor — impossible headlessly. This mirrors the existing `WasapiCaptureSourceProvider.CreateRemote`, which has no unit test either (the pure `RemoteCapturePlanner` is tested; the source construction is smoke). Task 2 unit-tests the decision; Task 5 unit-tests the marker via `FakeProvider`; Task 15 smoke-tests the real open-by-Id on hardware.

- [ ] **Step 1: Add the by-Id constructor + `DeviceId` to `MicCaptureSource`**

Refactor the two device-resolution entry points to share one private ctor (keeps the default path byte-identical):

```csharp
// src/LocalScribe.Core/Audio/MicCaptureSource.cs — replace the public constructor (lines 25-44)

    /// <summary>Friendly name of the capture device local.wav is recording from.</summary>
    public string DeviceName { get; }

    /// <summary>WASAPI device Id of the capture endpoint (design section 2): the id recorded into
    /// MicSnapshot when a pin was honored, empty-string-safe for the default path.</summary>
    public string DeviceId { get; }

    /// <param name="role">Which default capture endpoint to use. Communications = the "Default
    /// Communication Device" (matches how meeting apps route the mic); Multimedia/Console = the
    /// plain "Default Device".</param>
    public MicCaptureSource(IClock clock, Role role = Role.Communications)
        : this(clock, new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, role))
    {
    }

    /// <summary>Open a specific capture endpoint by its WASAPI device Id (design section 2:
    /// honoring a pinned mic). The provider only takes this path after MicCapturePlanner has
    /// confirmed the id is among the live devices; a raw NAudio failure here is a hardware race
    /// surfaced by the smoke run, not a unit-tested path.</summary>
    public MicCaptureSource(IClock clock, string deviceId)
        : this(clock, new MMDeviceEnumerator().GetDevice(deviceId))
    {
    }

    private MicCaptureSource(IClock clock, MMDevice device)
    {
        _clock = clock;
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
        _capture = new WasapiCapture(device);             // device mix format
        var fmt = _capture.WaveFormat;
        _channels = fmt.Channels;
        // Shared-mode mix format is effectively always 32-bit float, but validate so a non-float
        // endpoint fails loudly instead of writing garbage to local.wav.
        _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32;
        bool isPcm16 = fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16;
        if (!_isFloat && !isPcm16)
            throw new NotSupportedException(
                $"Unsupported mic mix format: {fmt.Encoding} {fmt.BitsPerSample}-bit, {fmt.Channels}ch. " +
                "Expected 32-bit IEEE float or 16-bit PCM.");
        _resampler = new MonoResampler16k(fmt.SampleRate);
        _capture.DataAvailable += OnData;
    }
```

(Leave the rest of the file — `OnData`, `DownmixToMono`, `Start`/`Stop`/`Dispose` — unchanged. `MMDevice` is already available via `using NAudio.CoreAudioApi;`.)

- [ ] **Step 2: Rewrite `WasapiCaptureSourceProvider` so `CreateMic` honors the pin**

```csharp
// src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs — full file
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using NAudio.CoreAudioApi;
namespace LocalScribe.Core.Live;

/// <summary>Thin adapter (Humble Object) over the real WASAPI sources. Re-plans on every call (a
/// Resume leg re-scans / re-resolves the device). Settings resolve through the injected provider at
/// capture-plan time (design 6.2), so a settings save between sessions takes effect at the next
/// Start/Resume. The mic now honors a pinned device (design section 2): MicCapturePlanner decides
/// from the live capture-device list whether to open by Id or fall back to the Communications
/// default, and the snapshot records what actually happened (incl. FellBackToDefault).</summary>
public sealed class WasapiCaptureSourceProvider : ICaptureSourceProvider
{
    private readonly Func<Settings> _settings;
    private readonly IAudioSessionScanner _scanner;
    private readonly ICaptureDeviceEnumerator _devices;

    public WasapiCaptureSourceProvider(Func<Settings> settingsProvider, IAudioSessionScanner scanner,
        ICaptureDeviceEnumerator? deviceEnumerator = null)
    {
        _settings = settingsProvider;
        _scanner = scanner;
        _devices = deviceEnumerator ?? new WasapiCaptureDeviceEnumerator();
    }

    /// <summary>Convenience overload: a fixed Settings snapshot (pre-Stage-4 call sites/tests).</summary>
    public WasapiCaptureSourceProvider(Settings settings, IAudioSessionScanner scanner)
        : this(() => settings, scanner)
    {
    }

    public (ICaptureSource Source, MicSnapshot Snapshot) CreateMic(IClock clock)
    {
        var plan = MicCapturePlanner.Plan(_settings().Mic, _devices.ListInputDevices());
        var mic = plan.Mode == MicMode.Pinned
            ? new MicCaptureSource(clock, plan.DeviceId!)
            : new MicCaptureSource(clock, Role.Communications);
        return (mic, new MicSnapshot
        {
            Mode = plan.Mode,
            Id = plan.Mode == MicMode.Pinned ? mic.DeviceId : null,
            Name = mic.DeviceName,
            FellBackToDefault = plan.FellBackToDefault,
        });
    }

    public (ICaptureSource Source, RemoteSnapshot Snapshot) CreateRemote(IClock clock)
    {
        var plan = RemoteCapturePlanner.Plan(_scanner.Scan(), _settings().Remote);
        ICaptureSource source = plan.Mode == RemoteMode.PerProcess
            ? new ProcessLoopbackCapture(plan.Pid!.Value, clock)
            : ProcessLoopbackCapture.SystemLoopbackExcludingSelf(clock);
        return (source, new RemoteSnapshot
        { Mode = plan.Mode, App = plan.App, FellBackToSystemMix = plan.FellBackToSystemMix });
    }
}
```

- [ ] **Step 3: Build the whole solution (this task's gate)**

Run: `dotnet build LocalScribe.slnx -c Debug --nologo`
Expected: 0 warnings / 0 errors. (Confirms the new ctor overloads bind at every existing call site — `CompositionRoot.cs:67` and `LiveRunner Program.cs:54` still use the 2-arg forms.)

- [ ] **Step 4: Run the full Core suite (no regression)**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj`
Expected: PASS except the 2 KNOWN fixture fails. No NEW failures.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Audio/MicCaptureSource.cs src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs
git commit -m "feat(core): CreateMic honors a pinned device (open by id, honest snapshot)"
```

---

### Task 5: `SessionController` emits the pin-unavailable marker

**Files:**
- Modify: `src/LocalScribe.Core/Live/SessionController.cs:297-301` (add a mic block after the remote-degraded block)
- Modify: `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs:99-118` (`FakeProvider` gains a settable `MicSnapshot`)
- Test: `tests/LocalScribe.Core.Tests/SessionControllerTests.cs` (add two facts)

**Interfaces:**
- Consumes: `MicSnapshot.FellBackToDefault` (Task 3); `Markers.PinnedMicUnavailable` (already exists, `Model/Markers.cs:14`); the existing `MarkerAt` + `outbox` marker seam the remote-degraded marker uses.
- Produces: at Start, when `micSnap.FellBackToDefault` is true, writes `Markers.PinnedMicUnavailable` to the outbox at `clock.ElapsedMs` and raises `Notice`. `FakeProvider` exposes `public MicSnapshot MicSnapshot` (settable, mirrors its existing `RemoteSnapshot` field) so tests drive the branch.

- [ ] **Step 1: Write the failing test**

```csharp
// add to tests/LocalScribe.Core.Tests/SessionControllerTests.cs
[Fact]
public async Task Pinned_mic_unavailable_emits_the_fallback_marker()
{
    var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root);
    provider.MicSnapshot = new MicSnapshot
    { Mode = MicMode.FollowDefault, Name = "Default Mic", FellBackToDefault = true };

    string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
    await c.StopAsync(CancellationToken.None);

    var stored = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
    Assert.Contains(stored, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.PinnedMicUnavailable);
}

[Fact]
public async Task Follow_default_mic_emits_no_fallback_marker()
{
    var (c, _, paths, _) = LiveTestDoubles.MakeController(_root);   // FakeProvider default: FellBackToDefault=false

    string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
    await c.StopAsync(CancellationToken.None);

    var stored = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
    Assert.DoesNotContain(stored, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.PinnedMicUnavailable);
}
```

> These mirror the existing `Lines_flow_to_LineInserted_and_transcript_jsonl` harness (read the finalized `transcript.jsonl` via `TranscriptStore.ReadAllAsync`). Ensure `SessionControllerTests` already has `using LocalScribe.Core.Model;` (it references `TranscriptKind`, `MicSnapshot`, `Markers`).

- [ ] **Step 2: Make `FakeProvider.CreateMic` return a settable snapshot**

```csharp
// tests/LocalScribe.Core.Tests/LiveTestDoubles.cs — in FakeProvider, add the field near RemoteSnapshot
    public MicSnapshot MicSnapshot = new() { Mode = MicMode.FollowDefault, Name = "Fake Mic" };
```

```csharp
// tests/LocalScribe.Core.Tests/LiveTestDoubles.cs — CreateMic returns the field instead of an inline literal
    public (ICaptureSource, MicSnapshot) CreateMic(IClock clock)
    { MicCreates++;
      ICaptureSource src = new FakeCaptureSource(SourceKind.Local, LocalFrames());
      if (ThrowOnLocalStop) src = new StopThrowingSource(src);
      LastMic = new DisposalTrackingSource(src);
      return (LastMic, MicSnapshot); }
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~SessionControllerTests.Pinned_mic_unavailable"`
Expected: FAIL — no `pinned microphone unavailable` marker is written (`SessionController` does not yet inspect `micSnap.FellBackToDefault`).

- [ ] **Step 4: Write minimal implementation**

```csharp
// src/LocalScribe.Core/Live/SessionController.cs — immediately AFTER the existing remote-degraded
// block (the `if (remoteSnap.FellBackToSystemMix) { ... }` ending at line 301), add:

                if (micSnap.FellBackToDefault)
                {
                    outbox.Writer.TryWrite(new MarkerAt(Markers.PinnedMicUnavailable, clock.ElapsedMs));
                    Notice?.Invoke("Pinned microphone unavailable - recording from the Windows Communications default instead.");
                }
```

(`micSnap` is in scope from line 192; `outbox`/`clock`/`MarkerAt`/`Notice` are the same seam the remote block above uses. Start-only by design — device hot-swap mid-recording is Stage 7, §7 out-of-scope.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~SessionControllerTests"`
Expected: PASS — both new facts plus the existing `SessionControllerTests` stay green.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/LiveTestDoubles.cs tests/LocalScribe.Core.Tests/SessionControllerTests.cs
git commit -m "feat(core): emit pinned-microphone-unavailable marker on mic fall-back"
```

---

## Phase 2 — App: overrides + composition

### Task 6: `MicOverride` — the per-session mic seam

**Files:**
- Create: `src/LocalScribe.App/Services/MicOverride.cs`
- Test: `tests/LocalScribe.App.Tests/MicOverrideTests.cs` (create)

**Interfaces:**
- Consumes: `MicSetting`, `Settings` (`Core.Model`).
- Produces: `MicOverride` with `MicSetting? Override { get; set; }` and `Settings Apply(Settings s)` — returns `s with { Mic = Override }` when set, else `s` unchanged. Holding a full `MicSetting` (not just a device id) lets a session override the persistent pin BACK to follow-default. Twin of `RemoteAppOverride`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.App.Tests/MicOverrideTests.cs
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The Record console's per-session mic override (design section 3), twin of
/// RemoteAppOverride: composes over the ONE live Func&lt;Settings&gt; and never writes settings.json.
/// A set Override wins (a device pin OR follow-default); unset is identity so the persistent
/// Settings pin stands.</summary>
public sealed class MicOverrideTests
{
    private static Settings Pinned(string id, string name) => new()
    { Mic = new MicSetting { Mode = MicMode.Pinned, Id = id, Name = name } };

    [Fact]
    public void Set_override_replaces_the_mic()
    {
        var settings = Pinned("id-saved", "Saved Studio Mic");
        var box = new MicOverride
        { Override = new MicSetting { Mode = MicMode.Pinned, Id = "id-session", Name = "Session Headset" } };

        var applied = box.Apply(settings);

        Assert.Equal("id-session", applied.Mic.Id);
        Assert.Equal(MicMode.Pinned, applied.Mic.Mode);
        Assert.NotSame(settings, applied);                       // new record, not a mutation
        Assert.Equal("id-saved", settings.Mic.Id);               // input untouched
    }

    [Fact]
    public void Override_can_force_follow_default_over_a_persistent_pin()
    {
        var settings = Pinned("id-saved", "Saved Studio Mic");
        var box = new MicOverride { Override = new MicSetting { Mode = MicMode.FollowDefault } };

        var applied = box.Apply(settings);

        Assert.Equal(MicMode.FollowDefault, applied.Mic.Mode);
        Assert.Null(applied.Mic.Id);
    }

    [Fact]
    public void Unset_override_is_identity()
    {
        var settings = Pinned("id-saved", "Saved Studio Mic");
        Assert.Same(settings, new MicOverride().Apply(settings));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~MicOverrideTests"`
Expected: FAIL — `MicOverride` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.App/Services/MicOverride.cs
using LocalScribe.Core.Model;
namespace LocalScribe.App.Services;

/// <summary>The Record console's per-session microphone override (design section 3), twin of
/// RemoteAppOverride. Set by the console's mic picker; cleared on Idle (session end). Holds a full
/// MicSetting (not just a device id) so a session can override the persistent pin BACK to
/// follow-default too. CompositionRoot composes Apply over the one live Func&lt;Settings&gt; that
/// SessionController and WasapiCaptureSourceProvider resolve at Start/Resume, so the override
/// reaches capture with zero Core changes and is NEVER persisted to settings.json. WPF-free.</summary>
public sealed class MicOverride
{
    /// <summary>The session's chosen mic, or null to let the persistent Settings pin (or
    /// follow-default) stand.</summary>
    public MicSetting? Override { get; set; }

    /// <summary>Returns settings with Mic replaced by the override when set, otherwise the input
    /// unchanged. Pure with respect to the input (records are immutable).</summary>
    public Settings Apply(Settings s) => Override is { } m ? s with { Mic = m } : s;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~MicOverrideTests"`
Expected: PASS (all three facts).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/Services/MicOverride.cs tests/LocalScribe.App.Tests/MicOverrideTests.cs
git commit -m "feat(app): MicOverride per-session mic seam (twin of RemoteAppOverride)"
```

---

### Task 7: `RemoteAppOverride.Apply` — a chosen app forces per-process

**Files:**
- Modify: `src/LocalScribe.App/Services/RemoteAppOverride.cs:22-26`
- Test: `tests/LocalScribe.App.Tests/RemoteAppOverrideTests.cs` (rewrite one fact, add one)

**Interfaces:**
- Produces: `Apply(Settings s)` now returns `s with { Remote = { Mode = PerProcess, App = override } }` whenever an override app is set — regardless of the base mode — so picking an app in Auto captures exactly that app for the session. Unset (null/empty) → identity (Auto's auto-detect / SystemMix stands). The per-process system-mix fallback for all-zeros/shared-audio images still applies downstream in `RemoteCapturePlanner`.

> **Why the old SystemMix concern no longer applies:** the console hides the app selector in SystemMix (`ShowAppSelector = Mode != SystemMix`, Task 11), and Task 11 only seeds the override in PerProcess mode — so an override is never set while the base is SystemMix. `Apply` stays a pure, mode-agnostic function; the UI is what constrains when an override exists.

- [ ] **Step 1: Update the failing tests**

Replace the `Auto_and_systemMix_modes_ignore_the_override` fact with a fact that pins the new forcing behavior, and keep the identity + no-persist facts:

```csharp
// tests/LocalScribe.App.Tests/RemoteAppOverrideTests.cs — replace the Auto/systemMix fact

    [Fact]
    public void Set_override_forces_per_process_from_any_base_mode()
    {
        var box = new RemoteAppOverride { App = "CiscoCollabHost" };
        var auto = new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.Auto, App = "Webex" } };

        var applied = box.Apply(auto);

        // Design section 5: an explicitly chosen app captures THAT app per-process, even from Auto.
        Assert.Equal(RemoteMode.PerProcess, applied.Remote.Mode);
        Assert.Equal("CiscoCollabHost", applied.Remote.App);
        Assert.Equal(RemoteMode.Auto, auto.Remote.Mode);            // input untouched
    }

    [Fact]
    public void Unset_override_leaves_the_base_mode_unchanged()
    {
        var box = new RemoteAppOverride();                          // null
        var auto = new Settings { Remote = new RemoteSetting { Mode = RemoteMode.Auto, App = "Webex" } };
        var mix = new Settings { Remote = new RemoteSetting { Mode = RemoteMode.SystemMix, App = "Webex" } };

        Assert.Same(auto, box.Apply(auto));                        // Auto's auto-detect stands
        Assert.Same(mix, box.Apply(mix));                          // SystemMix stands

        box.App = "";
        Assert.Same(auto, box.Apply(auto));                        // empty string is also identity
    }
```

Keep the existing `PerProcess_override_replaces_the_app`, `Null_empty_or_unset_override_is_identity` (it asserts identity for PerProcess-with-unset — still valid), and `Override_never_touches_the_settings_service` facts. Update the class doc comment to describe the widened behavior.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~RemoteAppOverrideTests"`
Expected: FAIL — `Set_override_forces_per_process_from_any_base_mode` fails (Apply currently only replaces App in PerProcess mode and never changes Mode).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.App/Services/RemoteAppOverride.cs — replace Apply (lines 22-26)

    /// <summary>Returns settings with Remote forced to PerProcess on the override app when one is
    /// set (design section 5: an explicitly chosen app is captured per-app for the session,
    /// regardless of the base mode), otherwise the input unchanged. Pure with respect to the input.
    /// The console only ever sets an override in a mode where the app selector is shown
    /// (Auto/PerProcess, never SystemMix), so this never falsifies a SystemMix session.</summary>
    public Settings Apply(Settings s)
        => _app is { Length: > 0 } app
            ? s with { Remote = s.Remote with { Mode = RemoteMode.PerProcess, App = app } }
            : s;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~RemoteAppOverrideTests"`
Expected: PASS (all facts).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/Services/RemoteAppOverride.cs tests/LocalScribe.App.Tests/RemoteAppOverrideTests.cs
git commit -m "feat(app): a chosen remote app forces per-process capture from any base mode"
```

---

### Task 8: `CompositionRoot` — inject the enumerator + layer `MicOverride`

**Files:**
- Modify: `src/LocalScribe.App/CompositionRoot.cs:16-26` (`AppComposition` record), `:53-95` (build body)
- Test: none new (composition; verified by the build gate + downstream VM tests in Tasks 9/12)

**Interfaces:**
- Produces: `AppComposition` gains `MicOverride MicOverride` and `ICaptureDeviceEnumerator DeviceEnumerator` members. `current` layers both overrides: `() => micOverride.Apply(remoteOverride.Apply(settingsService.Current))`. One shared `WasapiCaptureDeviceEnumerator` is passed to `WasapiCaptureSourceProvider` and exposed for the two VMs (Tasks 9, 12).

- [ ] **Step 1: Extend the `AppComposition` record**

```csharp
// src/LocalScribe.App/CompositionRoot.cs — add two members to AppComposition (after MatterSelection)
public sealed record AppComposition(
    SessionController Controller,
    ISettingsService Settings,
    StoragePaths Paths,
    MaintenanceService Maintenance,
    WindowRegistry Windows,
    IRecycleBin RecycleBin,
    string AppVersion,
    IDiarisationEngine Diarisation,
    RemoteAppOverride RemoteOverride,
    MatterSelectionOverride MatterSelection,
    MicOverride MicOverride,
    ICaptureDeviceEnumerator DeviceEnumerator);
```

(Add `using LocalScribe.Core.Live;` if not already present — it is, via existing `WasapiSessionScanner` usage.)

- [ ] **Step 2: Build the shared instances + layer the seam**

```csharp
// src/LocalScribe.App/CompositionRoot.cs — replace the override/current wiring (around lines 53-68)

        var remoteOverride = new RemoteAppOverride();
        var matterSelection = new MatterSelectionOverride();
        // Device selection (design section 3): one shared enumerator backs both the capture provider
        // and the Settings/console pickers. The per-session mic override layers over the SAME live
        // settings seam as the app override; both revert on Idle and never persist to settings.json.
        var micOverride = new MicOverride();
        var deviceEnumerator = new WasapiCaptureDeviceEnumerator();
        Func<Settings> current = () => micOverride.Apply(remoteOverride.Apply(settingsService.Current));

        var controller = new SessionController(paths, current, new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new LiveHardwareProbe(),
            new WasapiCaptureSourceProvider(current, new WasapiSessionScanner(), deviceEnumerator),
            () => new StopwatchClock(), TimeProvider.System, appVersion);
```

- [ ] **Step 3: Return the new members**

```csharp
// src/LocalScribe.App/CompositionRoot.cs — the final return
        return new AppComposition(controller, settingsService, paths, maintenance,
            new WindowRegistry(), recycleBin, appVersion, diarisation, remoteOverride, matterSelection,
            micOverride, deviceEnumerator);
```

- [ ] **Step 4: Build the whole solution**

Run: `dotnet build LocalScribe.slnx -c Debug --nologo`
Expected: 0 warnings / 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/CompositionRoot.cs
git commit -m "feat(app): compose MicOverride + shared capture-device enumerator"
```

---

## Phase 3 — App: Settings persistent pin

### Task 9: `SettingsPageViewModel` — the mic pin picker

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs:48-65` (ctor), `:158-165` (replace `MicDisplay`/`MicNote`)
- Modify: `src/LocalScribe.App/App.xaml.cs:132-141` (pass the enumerator)
- Modify: `tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs:25-34` (harness passes a fake enumerator)
- Test: `tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs` (add mic-picker facts)

**Interfaces:**
- Consumes: `ICaptureDeviceEnumerator`/`AudioDeviceInfo` (Task 1).
- Produces:
  - `public sealed record MicChoice(string? Id, string Name, string Label)` (top of the file, next to `LanguageChoice`).
  - `IReadOnlyList<MicChoice> MicChoices` — a leading follow-default choice (`Id: null`) then one per enumerated device; if the saved pin's `Id` is absent from the live list, a synthetic `"{Name} (not connected)"` choice (`Id` = saved id) is prepended and kept selected.
  - `MicChoice SelectedMic { get; set; }` (two-way): setting a device choice commits `Mic = { Pinned, Id, Name }`; setting the follow choice commits `Mic = { FollowDefault }`; via the existing `Commit`/`CommitAsync` auto-save chain.
  - ctor gains `ICaptureDeviceEnumerator deviceEnumerator` (added after `dispatch`, before the optional `modelsRoot`). Removes `MicDisplay`/`MicNote`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs — first, update MakeVm to inject a fake
// enumerator (default: two devices), then add these facts.

    private FakeCaptureDeviceEnumerator _devices =
        new(new LocalScribe.Core.Live.AudioDeviceInfo("id-headset", "Headset Microphone"),
            new LocalScribe.Core.Live.AudioDeviceInfo("id-webcam", "Webcam Mic"));

    [Fact]
    public async Task Selecting_a_device_pins_it()
    {
        var vm = MakeVm();
        var device = vm.MicChoices.First(c => c.Id == "id-headset");
        vm.SelectedMic = device;
        await vm.LastSave;
        Assert.Equal(MicMode.Pinned, _settings.Current.Mic.Mode);
        Assert.Equal("id-headset", _settings.Current.Mic.Id);
        Assert.Equal("Headset Microphone", _settings.Current.Mic.Name);
    }

    [Fact]
    public async Task Selecting_follow_default_clears_the_pin()
    {
        var vm = MakeVm(new Settings
        { Mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-headset", Name = "Headset Microphone" } });
        vm.SelectedMic = vm.MicChoices.First(c => c.Id is null);   // the follow-default choice
        await vm.LastSave;
        Assert.Equal(MicMode.FollowDefault, _settings.Current.Mic.Mode);
        Assert.Null(_settings.Current.Mic.Id);
    }

    [Fact]
    public void Absent_saved_pin_surfaces_not_connected_and_stays_selected()
    {
        var vm = MakeVm(new Settings
        { Mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-unplugged", Name = "Old USB Mic" } });
        Assert.Equal("id-unplugged", vm.SelectedMic.Id);
        Assert.Contains("not connected", vm.SelectedMic.Label);
    }

    [Fact]
    public void Enumeration_failure_leaves_only_follow_default()
    {
        _devices = new FakeCaptureDeviceEnumerator();              // empty list (enumeration failed)
        var vm = MakeVm();
        Assert.Single(vm.MicChoices);
        Assert.Null(vm.MicChoices[0].Id);
    }
```

Update `MakeVm` to pass `_devices` (and add `using LocalScribe.Core.Model;`, `using LocalScribe.Core.Tests;` if not present):

```csharp
    private SettingsPageViewModel MakeVm(Settings? initial = null)
    {
        if (initial is not null) _settings = new FakeSettingsService(initial);
        var maintenance = new Services.MaintenanceService(
            new StoragePaths(Path.Combine(_root, "storage")), _settings, new FakeRecycleBin(),
            TimeProvider.System);
        return new SettingsPageViewModel(_settings, maintenance, _launch,
            pickFolder: () => _pickResult, openFolder: _ => { }, _errors,
            dispatch: a => a(), _devices, modelsRoot: Path.Combine(_root, "models"));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~SettingsPageViewModelTests"`
Expected: FAIL — the ctor has no `ICaptureDeviceEnumerator` parameter; `MicChoices`/`SelectedMic`/`MicChoice` do not exist.

- [ ] **Step 3: Write minimal implementation**

Add the record next to `LanguageChoice`:

```csharp
// src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs — after the LanguageChoice record
/// <summary>One microphone option in the Settings pin picker (design section 4). Id null is the
/// "follow the Windows Communications default" choice; a device carries its WASAPI Id + friendly
/// Name; a saved-but-absent pin surfaces as a "(not connected)" Label kept selected (the pin is
/// never silently dropped - capture's own fall-back marker handles the real absence at Start).</summary>
public sealed record MicChoice(string? Id, string Name, string Label);
```

Inject the enumerator, build the list, expose `SelectedMic`. In the ctor, after the existing field assignment:

```csharp
// add the field
    private readonly ICaptureDeviceEnumerator _deviceEnumerator;
    private MicChoice _selectedMic;

// change the ctor signature (add deviceEnumerator before modelsRoot) and body
    public SettingsPageViewModel(ISettingsService settings, MaintenanceService maintenance,
        ILaunchAtLogin launchAtLogin, Func<string?> pickFolder, Action<string> openFolder,
        IUiErrorReporter errors, Action<Action> dispatch, ICaptureDeviceEnumerator deviceEnumerator,
        string? modelsRoot = null)
    {
        (_settings, _maintenance, _launchAtLogin, _pickFolder, _openFolder, _errors, _dispatch)
            = (settings, maintenance, launchAtLogin, pickFolder, openFolder, errors, dispatch);
        _deviceEnumerator = deviceEnumerator;
        _initialRoot = settings.Current.StorageRoot;
        ModelChoices = BuildModelChoices(modelsRoot ?? ModelPaths.ModelsRoot);
        MicChoices = BuildMicChoices(out _selectedMic);         // must precede any SelectedMic read

        PickStorageRootCommand = new RelayCommand(PickStorageRoot);
        // ... rest of the ctor unchanged ...
    }
```

Replace the old `MicDisplay`/`MicNote` block (lines 158-165) with the picker members:

```csharp
    public IReadOnlyList<MicChoice> MicChoices { get; }

    /// <summary>The selected mic. Setting a device pins it ({Pinned, Id, Name}); setting the
    /// follow-default choice clears the pin ({FollowDefault}). Auto-saves via the shared Commit
    /// chain (design section 4). A synthetic "(not connected)" choice for an absent saved pin is
    /// selectable-but-inert here: re-selecting it re-commits the same pin (harmless).</summary>
    public MicChoice SelectedMic
    {
        get => _selectedMic;
        set
        {
            if (value is null || value == _selectedMic) return;
            _selectedMic = value;
            Commit(s => s with
            {
                Mic = value.Id is null
                    ? new MicSetting { Mode = MicMode.FollowDefault }
                    : new MicSetting { Mode = MicMode.Pinned, Id = value.Id, Name = value.Name },
            });
            OnPropertyChanged();
        }
    }

    /// <summary>Build the picker: a leading follow-default choice, then one per live device. If the
    /// saved pin's device is absent, prepend a "(not connected)" choice and select it (never
    /// silently dropped). Selects the matching device / the follow choice otherwise.</summary>
    private IReadOnlyList<MicChoice> BuildMicChoices(out MicChoice selected)
    {
        var follow = new MicChoice(null, "", "Windows Communications default (follow)");
        var choices = new List<MicChoice> { follow };
        foreach (var d in _deviceEnumerator.ListInputDevices())
            choices.Add(new MicChoice(d.Id, d.Name, d.Name));

        var mic = _settings.Current.Mic;
        if (mic.Mode == MicMode.Pinned && !string.IsNullOrEmpty(mic.Id))
        {
            var match = choices.FirstOrDefault(c => c.Id == mic.Id);
            if (match is not null) { selected = match; return choices; }
            // Pinned device not present: prepend a "(not connected)" choice, keep it selected.
            var synthetic = new MicChoice(mic.Id, mic.Name ?? "", $"{mic.Name ?? "Pinned device"} (not connected)");
            choices.Insert(1, synthetic);
            selected = synthetic;
            return choices;
        }
        selected = follow;
        return choices;
    }
```

Update the class doc comment (lines 18-22) to drop the "read-only display / not available yet" wording. Ensure `using LocalScribe.Core.Live;` is present (it is — the file already uses `RemoteCapturePlanner`).

Now update the production construction site:

```csharp
// src/LocalScribe.App/App.xaml.cs — the settingsVm construction (add comp.DeviceEnumerator before dispatch's trailer)
        var settingsVm = new ViewModels.SettingsPageViewModel(comp.Settings, comp.Maintenance,
            new RegistryLaunchAtLogin(),
            pickFolder: () =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                { Title = "Choose the LocalScribe storage folder" };
                return dialog.ShowDialog() == true ? dialog.FolderName : null;
            },
            openFolder: p => System.Diagnostics.Process.Start("explorer.exe", p),
            errors, dispatch, comp.DeviceEnumerator);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~SettingsPageViewModelTests"`
Expected: PASS — new mic facts plus the existing Settings VM tests (there is also a reflection test at `:198` pinning HIDDEN fields; `MicDisplay`/`MicNote` removal must not trip it — the reflection test asserts *absence* of `RecordingIndicator`/`Hotkeys`/`AutoDetect`, not presence of Mic members, so it stays green).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs
git commit -m "feat(app): Settings microphone pin picker over the device enumerator"
```

---

### Task 10: `SettingsPage.xaml` — swap the mic row to a ComboBox

**Files:**
- Modify: `src/LocalScribe.App/SettingsPage.xaml:78-82`
- Test: build gate + manual smoke (Task 15, P-series)

**Interfaces:**
- Consumes: `MicChoices`/`SelectedMic`/`MicChoice.Label` (Task 9).

- [ ] **Step 1: Replace the read-only mic row with a ComboBox**

```xml
<!-- src/LocalScribe.App/SettingsPage.xaml — replace lines 78-82 (the Microphone TextBlock row + MicNote) -->
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Microphone" Style="{StaticResource FieldLabel}" />
                        <ComboBox ItemsSource="{Binding MicChoices}" DisplayMemberPath="Label"
                                  SelectedItem="{Binding SelectedMic, Mode=TwoWay}" MinWidth="240" />
                    </StackPanel>
                    <TextBlock Text="Recording follows the Windows Communications default unless you pin a device. A pinned device that is unplugged at record time falls back to the default and is noted in the transcript."
                               Style="{StaticResource Note}" TextWrapping="Wrap" />
```

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build LocalScribe.slnx -c Debug --nologo`
Expected: 0 warnings / 0 errors. (A dangling `{Binding MicDisplay}`/`{Binding MicNote}` would compile but fail at runtime — grep the file to confirm neither remains: `git grep -n "MicDisplay\|MicNote" src/LocalScribe.App/SettingsPage.xaml` returns nothing.)

- [ ] **Step 3: Commit**

```bash
git add src/LocalScribe.App/SettingsPage.xaml
git commit -m "feat(app): Settings mic row is a device ComboBox"
```

---

## Phase 4 — App: Record console

### Task 11: `RecordingConsoleViewModel` — remote-app availability in Auto

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs:43` (`ShowAppSelector`), `:83-84` (ctor seed), `:164-183` (Idle revert), `:185-195` (settings-changed re-seed)
- Test: `tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs` (add facts)

**Interfaces:**
- Consumes: `RemoteAppOverride.Apply` forcing (Task 7).
- Produces:
  - `ShowAppSelector => _settings.Current.Remote.Mode != RemoteMode.SystemMix` (visible in Auto + PerProcess; hidden only in SystemMix).
  - The override is seeded from the saved app ONLY when the base mode is PerProcess; in Auto it starts null (so an untouched Auto session keeps auto-detect — the override forces per-process only when the user actually picks an app). The Idle revert and settings-changed re-seed apply the same PerProcess-only guard.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs — add facts

    private static Settings Auto(string? app) => new()
    { Remote = new RemoteSetting { Mode = RemoteMode.Auto, App = app } };

    private static Settings SystemMix() => new()
    { Remote = new RemoteSetting { Mode = RemoteMode.SystemMix } };

    [Fact]
    public void App_selector_is_visible_in_auto_and_hidden_in_system_mix()
    {
        var (auto, _, _, _, _, _) = MakeConsole(Auto(null));
        Assert.True(auto.ShowAppSelector);

        var (mix, _, _, _, _, _) = MakeConsole(SystemMix());
        Assert.False(mix.ShowAppSelector);
    }

    [Fact]
    public void Auto_base_does_not_seed_the_override_until_the_user_picks()
    {
        var (console, _, _, over, _, _) = MakeConsole(Auto("Webex"));
        Assert.Null(over.App);                                     // untouched Auto -> auto-detect stands

        console.SessionTargetApp = "Zoom";                        // explicit pick
        Assert.Equal("Zoom", over.App);                           // now forces per-process (Task 7)
    }

    [Fact]
    public void PerProcess_base_still_seeds_the_override()
    {
        var (console, _, _, over, _, _) = MakeConsole(PerProcess("Webex"));
        Assert.Equal("Webex", console.SessionTargetApp);
        Assert.Equal("Webex", over.App);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~RecordingConsoleViewModelTests.App_selector_is_visible"`
Expected: FAIL — `ShowAppSelector` is still `Mode == PerProcess` (false in Auto), and the ctor seeds the override in Auto.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs — line 43
    public bool ShowAppSelector => _settings.Current.Remote.Mode != RemoteMode.SystemMix;
```

```csharp
// ctor (lines 83-84) — seed the override only when the base mode is PerProcess
        _sessionTargetApp = settings.Current.Remote.Mode == RemoteMode.PerProcess
            ? (settings.Current.Remote.App ?? "") : "";
        _remoteOverride.App = settings.Current.Remote.Mode == RemoteMode.PerProcess
            ? Normalize(_sessionTargetApp) : null;
```

```csharp
// OnSessionChanged Idle revert (lines 167-169) — same PerProcess-only guard when re-seeding
        if (Session.State == SessionState.Idle)
        {
            SessionTargetApp = _settings.Current.Remote.Mode == RemoteMode.PerProcess
                ? (_settings.Current.Remote.App ?? "") : "";
            _pickedMatterIds.Clear();
            // ... rest unchanged ...
```

> `OnSessionTargetAppChanged` (line 156) already sets `_remoteOverride.App = Normalize(value)` on every change, so assigning `SessionTargetApp = ""` on Idle correctly clears the override; and in PerProcess it re-seeds it. No extra code needed there.

```csharp
// OnSettingsChanged (lines 190-191) — re-seed an untouched selector only in PerProcess; in Auto keep it empty
            string newDefault = newSettings.Remote.Mode == RemoteMode.PerProcess
                ? (newSettings.Remote.App ?? "") : "";
            string oldDefault = oldSettings.Remote.Mode == RemoteMode.PerProcess
                ? (oldSettings.Remote.App ?? "") : "";
            if (SessionTargetApp == oldDefault) SessionTargetApp = newDefault;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~RecordingConsoleViewModelTests"`
Expected: PASS — new facts plus the existing console tests (`Seeds_selector_and_override_from_settings_at_construction` uses PerProcess, still green).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs
git commit -m "feat(app): Record console shows the app picker in Auto (hidden only in system-mix)"
```

---

### Task 12: `RecordingConsoleViewModel` — per-session mic override picker

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs:29-31` (fields), `:63-65` (`MicSummary`), `:77-88` (ctor), `:164-183` (Idle revert)
- Modify: `src/LocalScribe.App/App.xaml.cs:82-83` (pass the enumerator + `MicOverride`)
- Modify: `tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs:31-45` (harness passes the new deps)
- Test: `tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs` (add mic-override facts)

**Interfaces:**
- Consumes: `ICaptureDeviceEnumerator` (Task 1); `MicOverride` (Task 6); `MicChoice` (Task 9 — reuse the same record, referenced via `LocalScribe.App.ViewModels`).
- Produces:
  - ctor gains `ICaptureDeviceEnumerator deviceEnumerator` and `MicOverride micOverride` (added after `matterSelection`, before `dispatch`).
  - `IReadOnlyList<MicChoice> MicChoices` (follow-default + devices, same builder shape as Settings, no synthetic-pin needed — the console seeds from Settings.Mic) and `MicChoice SelectedMic { get; set; }` writing `_micOverride.Override` (a device → `{Pinned,Id,Name}`; follow → `{FollowDefault}`).
  - `MicSummary` reflects the override when set, else `Settings.Mic`.
  - Idle clears the override and re-seeds `SelectedMic` from `Settings.Mic`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs — update MakeConsole to build + inject
// a fake enumerator and a MicOverride, then return the override so tests can assert on it.

    private readonly FakeCaptureDeviceEnumerator _devices =
        new(new LocalScribe.Core.Live.AudioDeviceInfo("id-headset", "Headset Microphone"),
            new LocalScribe.Core.Live.AudioDeviceInfo("id-webcam", "Webcam Mic"));

    // extend the tuple with MicOverride Mic
    private (RecordingConsoleViewModel Console, FakeSettingsService Settings,
        SessionViewModel Session, RemoteAppOverride Override, MaintenanceService Maintenance,
        MatterSelectionOverride MatterSelection, MicOverride Mic) MakeConsole(Settings? initial = null)
    {
        var settings = new FakeSettingsService(initial ?? PerProcess("Webex"));
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, settings.Current, dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var over = new RemoteAppOverride();
        var maintenance = new MaintenanceService(new StoragePaths(_root), settings,
            new FakeRecycleBin(), TimeProvider.System);
        var matterSelection = new MatterSelectionOverride();
        var micOverride = new MicOverride();
        var console = new RecordingConsoleViewModel(settings, session, over, maintenance,
            matterSelection, _devices, micOverride, dispatch: a => a());
        return (console, settings, session, over, maintenance, matterSelection, micOverride);
    }

    [Fact]
    public void Selecting_a_console_mic_sets_the_override()
    {
        var (console, _, _, _, _, _, mic) = MakeConsole();
        console.SelectedMic = console.MicChoices.First(c => c.Id == "id-webcam");
        Assert.NotNull(mic.Override);
        Assert.Equal(MicMode.Pinned, mic.Override!.Mode);
        Assert.Equal("id-webcam", mic.Override.Id);
    }

    [Fact]
    public void Selecting_follow_default_overrides_a_saved_pin_back_to_default()
    {
        var (console, _, _, _, _, _, mic) = MakeConsole(new Settings
        {
            Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Webex" },
            Mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-headset", Name = "Headset Microphone" },
        });
        console.SelectedMic = console.MicChoices.First(c => c.Id is null);
        Assert.NotNull(mic.Override);
        Assert.Equal(MicMode.FollowDefault, mic.Override!.Mode);
    }

    [Fact]
    public void Ending_a_session_clears_the_mic_override()
    {
        var (console, _, session, _, _, _, mic) = MakeConsole();
        console.SelectedMic = console.MicChoices.First(c => c.Id == "id-webcam");
        Assert.NotNull(mic.Override);

        session.SetStateForTest(SessionState.Idle);   // however the existing tests drive Idle
        Assert.Null(mic.Override);
    }
```

> The `Ending_a_session_clears_the_mic_override` fact must trigger the console's `OnSessionChanged` Idle path exactly the way the existing "reverts the selector on Stop" test does — copy that test's mechanism for reaching `SessionState.Idle` (there is already an Idle-revert test in this file for the app selector; mirror it rather than inventing `SetStateForTest`). Every existing tuple destructuring `MakeConsole(...)` in this file must gain the trailing `_` for the new `Mic` member.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~RecordingConsoleViewModelTests"`
Expected: FAIL — the ctor has no `ICaptureDeviceEnumerator`/`MicOverride`; `MicChoices`/`SelectedMic` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs — add fields (near line 29-31)
    private readonly ICaptureDeviceEnumerator _deviceEnumerator;
    private readonly MicOverride _micOverride;
    private MicChoice _selectedMic;
```

```csharp
// ctor: extend the signature + body (lines 77-88)
    public RecordingConsoleViewModel(ISettingsService settings, SessionViewModel session,
        RemoteAppOverride remoteOverride, MaintenanceService maintenance,
        MatterSelectionOverride matterSelection, ICaptureDeviceEnumerator deviceEnumerator,
        MicOverride micOverride, Action<Action> dispatch)
    {
        (_settings, Session, _remoteOverride, _maintenance, _matterSelection, _dispatch)
            = (settings, session, remoteOverride, maintenance, matterSelection, dispatch);
        _deviceEnumerator = deviceEnumerator;
        _micOverride = micOverride;
        _sessionTargetApp = settings.Current.Remote.Mode == RemoteMode.PerProcess
            ? (settings.Current.Remote.App ?? "") : "";
        _remoteOverride.App = settings.Current.Remote.Mode == RemoteMode.PerProcess
            ? Normalize(_sessionTargetApp) : null;
        MicChoices = BuildMicChoices(out _selectedMic);
        ToggleMatterCommand = new RelayCommand<MatterPickRow>(ToggleMatter);
        settings.Changed += OnSettingsChanged;
        session.PropertyChanged += OnSessionChanged;
    }
```

```csharp
// MicSummary reflects the override when set, else Settings (replace lines 63-65)
    public string MicSummary
    {
        get
        {
            var mic = _micOverride.Override ?? _settings.Current.Mic;
            return mic.Mode == MicMode.Pinned
                ? "Microphone: pinned - " + (mic.Name ?? "(unnamed device)")
                : "Microphone: follows the Windows Communications default";
        }
    }

    public IReadOnlyList<MicChoice> MicChoices { get; }

    /// <summary>The per-session mic choice: writes MicOverride.Override (a device pin or
    /// follow-default) - never settings.json. Cleared on Idle. Seeded from Settings.Mic.</summary>
    public MicChoice SelectedMic
    {
        get => _selectedMic;
        set
        {
            if (value is null || value == _selectedMic) return;
            _selectedMic = value;
            _micOverride.Override = value.Id is null
                ? new MicSetting { Mode = MicMode.FollowDefault }
                : new MicSetting { Mode = MicMode.Pinned, Id = value.Id, Name = value.Name };
            OnPropertyChanged();
            OnPropertyChanged(nameof(MicSummary));
        }
    }

    /// <summary>Follow-default choice + one per live device, with the choice matching the current
    /// Settings.Mic selected (a saved pin whose device is absent falls back to follow-default in
    /// the seed - capture's own marker handles the real absence at Start).</summary>
    private IReadOnlyList<MicChoice> BuildMicChoices(out MicChoice selected)
    {
        var follow = new MicChoice(null, "", "Windows Communications default (follow)");
        var choices = new List<MicChoice> { follow };
        foreach (var d in _deviceEnumerator.ListInputDevices())
            choices.Add(new MicChoice(d.Id, d.Name, d.Name));

        var mic = _settings.Current.Mic;
        selected = mic.Mode == MicMode.Pinned && !string.IsNullOrEmpty(mic.Id)
            ? choices.FirstOrDefault(c => c.Id == mic.Id) ?? follow
            : follow;
        return choices;
    }
```

```csharp
// OnSessionChanged Idle branch (inside the existing `if (Session.State == SessionState.Idle)`) —
// clear the mic override and re-seed the console picker, alongside the existing app-selector revert:
            _micOverride.Override = null;
            _selectedMic = BuildSelectedFromSettings();
            OnPropertyChanged(nameof(SelectedMic));
            OnPropertyChanged(nameof(MicSummary));
```

Add the small helper used above (re-seeds selection from Settings without rebuilding the list):

```csharp
    private MicChoice BuildSelectedFromSettings()
    {
        var mic = _settings.Current.Mic;
        return mic.Mode == MicMode.Pinned && !string.IsNullOrEmpty(mic.Id)
            ? MicChoices.FirstOrDefault(c => c.Id == mic.Id) ?? MicChoices[0]
            : MicChoices[0];
    }
```

Update the production construction site:

```csharp
// src/LocalScribe.App/App.xaml.cs — the console construction (lines 82-83)
        var console = new ViewModels.RecordingConsoleViewModel(comp.Settings, session,
            comp.RemoteOverride, comp.Maintenance, comp.MatterSelection,
            comp.DeviceEnumerator, comp.MicOverride, dispatch);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~RecordingConsoleViewModelTests"`
Expected: PASS — new mic facts plus the existing console tests (updated tuple destructurings).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs
git commit -m "feat(app): Record console per-session mic override picker"
```

---

### Task 13: `LiveViewWindow.xaml` — the console mic ComboBox

**Files:**
- Modify: `src/LocalScribe.App/LiveViewWindow.xaml:21-33` (add a mic row beside the app selector)
- Test: build gate + manual smoke (Task 15)

**Interfaces:**
- Consumes: `Console.MicChoices`/`Console.SelectedMic` (Task 12).

- [ ] **Step 1: Add the mic picker row**

Insert a mic-picker row immediately after the `MicSummary` TextBlock (line 21-22), before the app-selector `StackPanel` (line 23):

```xml
<!-- src/LocalScribe.App/LiveViewWindow.xaml — after the Console.MicSummary TextBlock (line 22) -->
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,0,0,12">
                    <TextBlock Text="Microphone" VerticalAlignment="Center" Margin="0,0,8,0" />
                    <ComboBox MinWidth="220" ItemsSource="{Binding Console.MicChoices}"
                              DisplayMemberPath="Label"
                              SelectedItem="{Binding Console.SelectedMic, Mode=TwoWay}" />
                </StackPanel>
```

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build LocalScribe.slnx -c Debug --nologo`
Expected: 0 warnings / 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/LocalScribe.App/LiveViewWindow.xaml
git commit -m "feat(app): Record console mic ComboBox in the idle panel"
```

---

## Phase 5 — Docs + smoke

### Task 14: Spec §12 deltas

**Files:**
- Modify: `docs/specs/localscribe-specs.md` (§12 device config; the mic-snapshot / marker references)

**Interfaces:** none (documentation).

- [ ] **Step 1: Update §12 to reflect the shipped feature**

Edit `docs/specs/localscribe-specs.md` §12 so it states the now-true behavior (design §9). Add/adjust the following points (match the surrounding prose style; these are the required facts):

- The microphone picker EXISTS: a persistent pin in Settings (`mic.mode = pinned {id,name}`) and a per-session override in the Record console (reverts on Idle, can override a pin back to follow-default).
- Capture HONORS `mic.mode = pinned` by opening the device by `id`; a pinned device absent at Start falls back to the Windows Communications default AND writes the `pinned microphone unavailable → default` marker (never a silent rebind). `session.json devices.mic` records the device actually captured plus `fellBackToDefault`.
- Remote-app selection is available in Auto + Per-process (hidden only in full system-mix); an explicitly chosen app is captured per-app for that session.
- No `settings.json` schema change (`mic`/`remote` shapes unchanged; `MicSnapshot.fellBackToDefault` is an additive session.json field).

- [ ] **Step 2: Commit**

```bash
git add docs/specs/localscribe-specs.md
git commit -m "docs(spec): section 12 device config - mic picker + remote-app availability shipped"
```

---

### Task 15: Hardware + GUI smoke runbook

**Files:**
- Create: `docs/plans/2026-07-08-device-selection-smoke-runbook.md`

**Interfaces:** none (the manual verification the unit tests cannot cover — the real open-by-Id, fall-back, and marker on hardware; the two ComboBoxes in a running app).

- [ ] **Step 1: Write the runbook**

Create `docs/plans/2026-07-08-device-selection-smoke-runbook.md` covering (each step: action → expected):

```markdown
# Device Selection smoke runbook (2026-07-08)

Prereq: build + run the real app (close any running LocalScribe.App.exe first, then
`dotnet build LocalScribe.slnx -c Debug --nologo` and launch LocalScribe.App). At least two
input devices connected (e.g. laptop mic + a USB headset).

## Part A - Settings persistent pin
- A1. Settings > Recording > Microphone: the dropdown lists "Windows Communications default
      (follow)" plus every connected input device by friendly name. Expected: all mics present.
- A2. Pin the USB headset; reopen Settings. Expected: settings.json `mic` = {mode:"pinned",
      id, name}; the dropdown re-opens with the headset selected.
- A3. Record a short session. Expected: session.json `devices.mic` = {mode:"pinned", id, name}
      of the headset, `fellBackToDefault:false`; local.wav is the headset's audio.

## Part B - Pinned-mic-gone fall-back + marker (the evidentiary path)
- B1. With the headset still pinned, UNPLUG it. Reopen Settings. Expected: the dropdown shows
      "{headset name} (not connected)" and keeps it selected (pin never silently dropped).
- B2. Record a short session with the headset unplugged. Expected: capture uses the
      Communications default; the transcript contains the `pinned microphone unavailable ->
      default` marker; session.json `devices.mic.mode` = "followDefault",
      `fellBackToDefault:true`; a tray/console Notice appeared.
- B3. Re-plug the headset, record again. Expected: back to Part A3 behavior (pinned, no marker).

## Part C - Record console per-session override
- C1. Set Settings mic to follow-default. Open the Record console. Expected: the Microphone
      dropdown shows follow-default selected; MicSummary reads "follows the Windows
      Communications default".
- C2. In the console, pick the headset; Start; Stop. Expected: THAT session captured the
      headset (session.json devices.mic); Settings mic is STILL follow-default (override never
      persisted).
- C3. After Stop (Idle), reopen the console. Expected: the mic dropdown reverted to
      follow-default (the per-session override cleared).
- C4. Pin the headset in Settings, then in the console override BACK to follow-default; record.
      Expected: that session captured the default; Settings still shows the headset pinned.

## Part D - Remote-app availability in Auto
- D1. Settings > Remote capture = Auto. Open the console. Expected: the "Record this app"
      selector is VISIBLE (was hidden pre-change) and empty.
- D2. With Webex/Zoom running, type/pick that app in the console; Start. Expected: session.json
      `devices.remote` = {mode:"perProcess", app:<that app>} - the chosen app captured per-app
      even though the base mode is Auto.
- D3. Set Remote capture = System mix. Open the console. Expected: the app selector is HIDDEN.
```

- [ ] **Step 2: Commit**

```bash
git add docs/plans/2026-07-08-device-selection-smoke-runbook.md
git commit -m "docs: device-selection hardware + GUI smoke runbook"
```

---

## Final gate (run once, after Task 15)

- [ ] **Full build:** `dotnet build LocalScribe.slnx -c Debug --nologo` → 0 warnings / 0 errors (close `LocalScribe.App.exe` first).
- [ ] **Core suite:** `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj` → green except the 2 KNOWN fixture fails; no NEW failures.
- [ ] **App suite:** `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj` → all green.
- [ ] Hand the smoke runbook (Task 15) to the user — the real open-by-Id / fall-back / marker on hardware and the two ComboBoxes in the running app are user-verified, not automated.

---

## Self-Review

**Spec coverage (design §0–§10):**
- §1 enumeration (Core) → Task 1.
- §2 capture honors a pinned mic + fall-back + marker → Tasks 2 (decision), 3 (snapshot flag), 4 (open-by-id + CreateMic), 5 (marker).
- §3 per-session mic override (App) → Task 6 (service), 8 (compose), 12 (console picker).
- §4 Settings persistent pin → Tasks 9 (VM), 10 (XAML).
- §5 Record console: mic override + remote-app availability + chosen-app-forces-per-process → Tasks 7 (Apply), 11 (availability), 12 (mic), 13 (XAML).
- §6 data flow/persistence (no schema bump) → Task 3 (additive flag), 8 (composed `current`).
- §7 error handling (enum failure → follow-only; absent pin → marker) → Task 1 (empty-on-throw), 2/5 (fall-back+marker), 9 ("(not connected)").
- §8 testing split (thin adapters smoke, planner/VM unit) → honored throughout; Task 15 smoke.
- §9 spec deltas → Task 14.
- §10 out-of-scope (hot-swap, refresh button, render/loopback selection, remote-mode-from-console, persisted override) → not implemented; Start-only resolution noted in Task 5.

**Placeholder scan:** every code step carries complete code; every test step carries the assertions; no "TBD"/"add validation"/"similar to Task N".

**Type consistency:** `MicChoice(string? Id, string Name, string Label)` is defined once (Task 9) and reused by the console (Task 12). `MicPlan(Mode, DeviceId, FellBackToDefault)` (Task 2) feeds `CreateMic` (Task 4). `MicSnapshot.FellBackToDefault` (Task 3) is produced by `CreateMic` (Task 4) and consumed by `SessionController` (Task 5) and `FakeProvider` (Task 5). `MicOverride.Override : MicSetting?` / `.Apply` (Task 6) is layered in `CompositionRoot.current` (Task 8) and written by the console (Task 12). `ICaptureDeviceEnumerator.ListInputDevices()` (Task 1) is consumed by `MicCapturePlanner` (Task 2), `WasapiCaptureSourceProvider` (Task 4), and both VMs (Tasks 9, 12); `AppComposition.DeviceEnumerator`/`MicOverride` (Task 8) are the wiring the VMs read.
