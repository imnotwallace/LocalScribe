# Import-time Speaker Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Auto-diarise single-leg audio imports (mono / 2-ch downmix / >2-ch) so an imported call no longer renders as one speaker called "Me", and let the user name the detected speakers without paying for a second diarisation.

**Architecture:** Approach A from the spec — App-layer orchestration *after* `AudioImporter.ImportAsync` returns. A new `SpeakerDetectionStep` service probes the retained 16 kHz mono FLAC leg, calls the existing out-of-process sherpa diariser, assigns clusters to transcript lines, and commits through the already-proven headless `MaintenanceService.SaveDiarisationAsync`. Running after the import is atomically complete keeps a diariser failure structurally unable to reach `AudioImporter`'s delete-the-whole-session catch, and avoids the Save-stage session.json clobber window.

**Tech Stack:** .NET 10 (`net10.0-windows`), WPF + CommunityToolkit.Mvvm, xunit 2.9.3, sherpa-onnx via the out-of-process `LocalScribe.Diarizer.exe` helper, PowerShell 7 for tooling guards.

**Spec:** `docs/superpowers/specs/2026-07-28-import-auto-diarisation-design.md` (committed @ `37efaad`)

## Global Constraints

- **Test framework is xunit 2.9.3 with `Assert.*` only.** There is no Moq, NSubstitute, FluentAssertions, Shouldly or AutoFixture anywhere in the repo. Every double is a hand-written `private sealed class Fake*` nested in the test file. Do not introduce a mocking library.
- **`tests/LocalScribe.App.Tests` has no global `using Xunit;`** (unlike Core.Tests and Mcp.Tests, whose csproj carry `<Using Include="Xunit" />`). App.Tests files must write `using Xunit;` explicitly.
- **New VM tests MUST use a queued dispatch fake**, not the synchronous `a => a()`. Production dispatch is `a => Dispatcher.BeginInvoke(a)`; the assistant-surfaces round shipped a Critical stamp-ordering bug that a synchronous fake masked. Copy the canonical `QueuedDispatch` (with `PumpOne`) from `tests/LocalScribe.App.Tests/SplitSpeakersViewModelVoiceprintTests.cs:29-42` into your own test file — it is deliberately duplicated per file, there is no shared helper.
- **Never `System.Progress<T>`** in a VM or a test. It captures a `SynchronizationContext` headless tests do not have. Use the house `DispatchedProgress` nested class (`SplitSpeakersViewModel.cs:411-416`) in production and an inline `SynchronousProgress<T>` (`AudioImporterTests.cs:405-410`) in tests.
- **No Unicode emojis in any test script or tool script** (global user rule).
- **Evidentiary rules (locked):** never delete or rewrite transcript content; degradation is never silent; `SaveDiarisationAsync` must never touch audio for any `AudioRetention` value; the app never auto-assigns a name from a voiceprint match.
- **Widening `AudioImporter`'s constructor is done by adding a NEW TRAILING OPTIONAL PARAMETER WITH A DEFAULT** — the house convention. There are exactly 3 construction sites: `tests/LocalScribe.Core.Tests/AudioImporterTests.cs:87`, `tests/LocalScribe.Core.Tests/AudioImportFixtureTests.cs:69`, `src/LocalScribe.App/App.xaml.cs:586`. This plan does not change that ctor at all.
- **`ExternalEngineBusy` is a settable `Func<string?>?` property on the concrete `SessionController` (`SessionController.cs:171`), not an event.** Writers must capture-then-chain (`App.xaml.cs:580-581`), never clobber. A bare assignment placed after line 581 silently drops the audio-import lane.
- **Test commands.** Per-task: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~XxxTests" 2>&1 | tail -5`. Final gate: `dotnet build LocalScribe.slnx` then `dotnet test tests/LocalScribe.Core.Tests`, `dotnet test tests/LocalScribe.App.Tests`, `dotnet test tests/LocalScribe.Mcp.Tests`.
- **Known baseline:** `Core.Tests` has 2 known environment failures (`DiarisationFixtureTests`, `GoldenCorpusFixtureTests`) when the private fixture corpora are absent. `App.Tests` and `Mcp.Tests` must be fully green.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/LocalScribe.Core/Import/AudioImporter.cs` | `SpeakerDetection` enum, two `ImportRequest` init props + validation, `ImportStage.DetectSpeakers` | 1 |
| `src/LocalScribe.Core/Import/ChannelMapper.cs` | `Downmixed` flag now true for 2-ch downmix, not only >2-ch | 2 |
| `src/LocalScribe.Core/Model/Markers.cs` | `SpeakerDetectionFailed`, `SpeakerDetectionOneVoice`, `SpeakerDetectionNoAudio` | 2, 5 |
| `src/LocalScribe.Core/Diarisation/DiarisationModels.cs` **(new)** | The two sherpa model filenames, hoisted from three duplicated literals | 3 |
| `src/LocalScribe.App/Services/DiarisationAvailability.cs` **(new)** | Pure-ish probe: is the helper exe + both models present? Returns a user-facing reason or null | 3 |
| `src/LocalScribe.App/Services/MaintenanceService.cs` | New `RenameSpeakersAsync` — a names-only write path that never restamps the diarisation | 4 |
| `src/LocalScribe.App/Services/SpeakerDetectionStep.cs` **(new)** | The whole post-import detection phase: probe leg → diarise → assign → commit → counts → markers | 5 |
| `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs` | Source-gate relaxation; hydrate `Clusters` from `speakers.json`; engine-gate probe | 6, 7, 11 |
| `src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs` | Speakers control members; availability gate; `DetectSpeakers` stage text + progress; `ImportRunner` delegate | 8 |
| `src/LocalScribe.App/ImportDialog.xaml` | The Speakers row, the channel note, the unavailable reason, the detect progress row | 8 |
| `src/LocalScribe.App/App.xaml.cs` | Two-phase `importRunner`; `ImportLaneState`; completion routing; `NotifyRosterChanged` wiring | 9, 10 |
| `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs` | Engine-gate probe on the voiceprint backfill scan | 11 |
| `tools/verify-diarizer.ps1` **(new)** | Publish guard: the exe is present AND the ORT collision DLLs are absent beside the App binary | 12 |

---

### Task 1: `SpeakerDetection` request types + `ImportStage.DetectSpeakers`

**Files:**
- Modify: `src/LocalScribe.Core/Import/AudioImporter.cs:11-30`
- Test: `tests/LocalScribe.Core.Tests/ImportRequestSpeakerDetectionTests.cs` (create)

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `public enum SpeakerDetection { Off, Auto, Declared }` in namespace `LocalScribe.Core.Import`; `ImportRequest.SpeakerDetection` (init, defaults `Off`) and `ImportRequest.SpeakerCount` (`int?`, init); `ImportStage.DetectSpeakers`. Tasks 5, 8 and 9 depend on all four names exactly as spelled here.

**Context you need:** `ImportRequest` is a `sealed record` with `required` and defaulted init props (`AudioImporter.cs:14-26`). `ImportStage` is `public enum ImportStage { Copy, Decode, Transcribe, Save }` at `AudioImporter.cs:30`.

The validation is load-bearing, not defensive. `SherpaDiarisationRunner.cs:23` branches on `if (forcedClusterCount is int k && k > 0)` — a `0` silently falls through to the untuned auto path while the UI claims it forced a count.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.Core.Tests/ImportRequestSpeakerDetectionTests.cs`:

```csharp
using LocalScribe.Core.Import;

namespace LocalScribe.Core.Tests;

/// <summary>Import-time speaker detection request shape (design 2026-07-28 section 2).
/// The count validation is load-bearing, not defensive: SherpaDiarisationRunner.cs:23 branches on
/// `forcedClusterCount is int k && k > 0`, so an unvalidated 0 would silently take the AUTO
/// threshold path while the dialog claimed it forced a count. These tests keep that unreachable.</summary>
public sealed class ImportRequestSpeakerDetectionTests
{
    private static ImportRequest Base() => new()
    {
        SourcePath = @"C:\x\a.wav",
        Title = "T",
        RecordedAtLocal = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Defaults_to_off_with_no_count()
    {
        var r = Base();
        Assert.Equal(SpeakerDetection.Off, r.SpeakerDetection);
        Assert.Null(r.SpeakerCount);
    }

    [Fact]
    public void Auto_carries_no_count()
    {
        var r = Base() with { SpeakerDetection = SpeakerDetection.Auto };
        Assert.Equal(SpeakerDetection.Auto, r.SpeakerDetection);
        Assert.Null(r.SpeakerCount);
    }

    [Fact]
    public void Declared_accepts_two_or_more()
    {
        var r = Base() with { SpeakerDetection = SpeakerDetection.Declared, SpeakerCount = 3 };
        Assert.Equal(3, r.SpeakerCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public void Declared_rejects_a_count_below_two(int? count)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Base() with { SpeakerDetection = SpeakerDetection.Declared, SpeakerCount = count });
        Assert.Contains("SpeakerCount", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SpeakerDetection.Off)]
    [InlineData(SpeakerDetection.Auto)]
    public void A_count_without_declared_is_rejected(SpeakerDetection mode)
    {
        Assert.Throws<ArgumentException>(() =>
            Base() with { SpeakerDetection = mode, SpeakerCount = 2 });
    }

    [Fact]
    public void DetectSpeakers_is_a_distinct_stage()
    {
        Assert.NotEqual(ImportStage.Save, ImportStage.DetectSpeakers);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~ImportRequestSpeakerDetectionTests" 2>&1 | tail -5`
Expected: FAIL — compile errors, `SpeakerDetection` and `ImportStage.DetectSpeakers` do not exist.

- [ ] **Step 3: Write the implementation**

In `src/LocalScribe.Core/Import/AudioImporter.cs`, add above `ImportRequest` (before line 11):

```csharp
/// <summary>Import-time speaker detection (design 2026-07-28). <c>Off</c> runs no diarisation pass
/// at all and is the record default, so every pre-existing caller behaves exactly as before.
/// <c>Auto</c> maps to DiarisationRequest.ForcedClusterCount = null (sherpa threshold clustering);
/// <c>Declared</c> maps to ForcedClusterCount = SpeakerCount.</summary>
public enum SpeakerDetection { Off, Auto, Declared }
```

Inside `ImportRequest`, after the `Language` property (line 25), add:

```csharp
    /// <summary>Import-time speaker detection mode (design 2026-07-28). Off = no diarisation pass.</summary>
    public SpeakerDetection SpeakerDetection
    {
        get;
        init
        {
            field = value;
            ValidateSpeakerDetection(value, SpeakerCount);
        }
    } = SpeakerDetection.Off;

    /// <summary>The declared voice count; required (>= 2) when SpeakerDetection == Declared and
    /// must be null otherwise. NOT merely defensive: SherpaDiarisationRunner.cs:23 branches on
    /// `forcedClusterCount is int k && k > 0`, so an unvalidated 0 would silently take the AUTO
    /// threshold path while the dialog claimed it forced a count.</summary>
    public int? SpeakerCount
    {
        get;
        init
        {
            field = value;
            ValidateSpeakerDetection(SpeakerDetection, value);
        }
    }

    private static void ValidateSpeakerDetection(SpeakerDetection mode, int? count)
    {
        if (mode == SpeakerDetection.Declared)
        {
            if (count is not int n || n < 2)
                throw new ArgumentException(
                    "SpeakerCount must be 2 or more when SpeakerDetection is Declared.",
                    nameof(SpeakerCount));
        }
        else if (count is not null)
        {
            throw new ArgumentException(
                $"SpeakerCount must be null when SpeakerDetection is {mode}.", nameof(SpeakerCount));
        }
    }
```

> **Note on `field`:** the C# 14 `field` keyword is available on `net10.0`. If the build rejects it, fall back to explicit backing fields (`private readonly SpeakerDetection _speakerDetection = SpeakerDetection.Off;` and `private readonly int? _speakerCount;`) with the same init-accessor bodies. Validating in *both* init accessors is what makes order-independence work: `with { SpeakerDetection = Declared, SpeakerCount = 3 }` and `with { SpeakerCount = 3, SpeakerDetection = Declared }` must both succeed, and each single-property set must be checked against the other's current value.

Then change line 30:

```csharp
public enum ImportStage { Copy, Decode, Transcribe, Save, DetectSpeakers }
```

Update the doc comment above it to name the new stage:

```csharp
/// <summary>The staged-progress vocabulary (design 2026-07-13 section 4.4): reported once at the
/// START of each stage. DetectSpeakers (design 2026-07-28) is reported by the App-layer runner
/// AFTER ImportAsync returns, so the observed order is Copy -> Decode -> Transcribe -> Save ->
/// DetectSpeakers.</summary>
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~ImportRequestSpeakerDetectionTests" 2>&1 | tail -5`
Expected: PASS, 8 tests.

- [ ] **Step 5: Confirm no existing caller broke**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~AudioImporterTests" 2>&1 | tail -5`
Expected: PASS. `SpeakerDetection.Off` being the default means no existing `ImportRequest` construction changes behaviour.

Also confirm the new enum member did not silently change any stage rendering yet:

Run: `dotnet build LocalScribe.slnx 2>&1 | tail -5`
Expected: build succeeds. (`ImportDialogViewModel`'s stage switch has a `_ =>` catch-all, so the compiler will NOT flag the new member — Task 8 fixes that by hand.)

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Import/AudioImporter.cs tests/LocalScribe.Core.Tests/ImportRequestSpeakerDetectionTests.cs
git commit -m "feat(import): SpeakerDetection request mode + DetectSpeakers stage

Declared rejects a count below 2 at construction. Not defensive:
SherpaDiarisationRunner.cs:23 branches on 'k > 0', so an unvalidated 0
would silently take the auto threshold path while the dialog claimed it
forced a count.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YLjXhg7YDw7x6EHfGyUuYk"
```

---

### Task 2: 2-channel downmix marker (adjacent fix 6)

**Files:**
- Modify: `src/LocalScribe.Core/Import/ChannelMapper.cs:14-16, 31, 34`
- Modify: `src/LocalScribe.Core/Import/AudioImporter.cs:159` (rename the flag reference)
- Modify: `src/LocalScribe.Core/Model/Markers.cs:45`
- Test: `tests/LocalScribe.Core.Tests/ChannelMapperTests.cs` (extend if present, else create)

**Interfaces:**
- Consumes: nothing.
- Produces: `ChannelMapPlan.Downmixed` (renamed from `DownmixedMultichannel`). No later task depends on it; this is a self-contained evidentiary fix.

**Context you need:** `ChannelMapper.Plan` (`ChannelMapper.cs:24-35`) currently sets `DownmixedMultichannel: decodedChannels > 2`. A 2-channel two-party call imported without ticking "each party is on their own channel" therefore becomes one mixed mono leg with **nothing** recorded in the transcript. That is now the primary path this feature serves. The existing marker text (`Markers.cs:48-49`, `"imported audio downmixed to mono: source had {0} channels"`) already reads correctly for 2.

Mono (`decodedChannels == 1`) must **not** produce a marker — nothing was downmixed.

- [ ] **Step 1: Write the failing test**

Check first whether the file exists: `ls tests/LocalScribe.Core.Tests/ChannelMapperTests.cs`. If it exists, append these methods to the existing class and skip the header. Otherwise create the file:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Import;

namespace LocalScribe.Core.Tests;

/// <summary>Channel mapping plan shape (design 2026-07-13 section 4.3) + the 2-channel downmix
/// marker (design 2026-07-28 adjacent fix 6). Before that fix the Downmixed flag was
/// `decodedChannels > 2`, so a stereo two-party call imported WITHOUT ticking "each party is on
/// their own channel" silently collapsed into one mixed mono leg with nothing recording it -
/// exactly the case import-time speaker detection now serves.</summary>
public sealed class ChannelMapperDownmixMarkerTests
{
    [Fact]
    public void Two_channel_downmix_is_marked()
    {
        var plan = ChannelMapper.Plan(2, StereoMapping.Downmix);
        Assert.True(plan.Downmixed);
        Assert.Single(plan.Legs);
        Assert.Equal(SourceKind.Local, plan.Legs[0].Kind);
    }

    [Fact]
    public void Multichannel_downmix_is_still_marked()
    {
        Assert.True(ChannelMapper.Plan(6, StereoMapping.Downmix).Downmixed);
        // >2 channels ignore the stereo answer entirely - there is no two-leg split to make.
        Assert.True(ChannelMapper.Plan(6, StereoMapping.Split).Downmixed);
    }

    [Fact]
    public void Mono_is_not_marked_because_nothing_was_downmixed()
    {
        var plan = ChannelMapper.Plan(1, StereoMapping.Downmix);
        Assert.False(plan.Downmixed);
        Assert.Single(plan.Legs);
    }

    [Theory]
    [InlineData(StereoMapping.Split)]
    [InlineData(StereoMapping.SplitSwapped)]
    public void A_two_channel_split_is_not_a_downmix(StereoMapping stereo)
    {
        var plan = ChannelMapper.Plan(2, stereo);
        Assert.False(plan.Downmixed);
        Assert.Equal(2, plan.Legs.Count);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~ChannelMapperDownmixMarkerTests" 2>&1 | tail -5`
Expected: FAIL — `Downmixed` does not exist (the property is still `DownmixedMultichannel`), so this is a compile error.

- [ ] **Step 3: Write the implementation**

In `src/LocalScribe.Core/Import/ChannelMapper.cs`, replace the doc comment and record at lines 14-16:

```csharp
/// <summary>Downmixed flags a source that was averaged down to one mono leg - either a 2-channel
/// stereo file the user did NOT declare as one-party-per-channel, or any >2-channel source. The
/// importer surfaces it as Markers.ImportedDownmixed (degradation is never silent). Widened from
/// >2-channel-only in design 2026-07-28 adjacent fix 6: a stereo two-party call imported without
/// ticking the box silently became one mixed mono leg with nothing recording it.</summary>
public sealed record ChannelMapPlan(IReadOnlyList<LegPlan> Legs, bool Downmixed);
```

At line 31 (the two-leg split branch):

```csharp
                Downmixed: false);
```

At line 34 (the single-leg branch):

```csharp
            Downmixed: decodedChannels > 1);
```

In `src/LocalScribe.Core/Import/AudioImporter.cs:159`:

```csharp
            if (plan.Downmixed)
```

In `src/LocalScribe.Core/Model/Markers.cs`, update the comment at line 45 so it stops implying multichannel-only:

```csharp
    // (claimed, decoded); {0} in ImportedDownmixed is the decoded channel count (2 for a stereo
    // file the user did not declare as one-party-per-channel; more for a multichannel source).
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~ChannelMapper" 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Run the importer suite for the rename**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~AudioImporter" 2>&1 | tail -5`
Expected: PASS. If a test asserted the *absence* of a downmix marker for a 2-channel downmix import, that assertion was pinning the bug — update it to expect the marker and add a comment naming design 2026-07-28 adjacent fix 6.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Import/ChannelMapper.cs src/LocalScribe.Core/Import/AudioImporter.cs src/LocalScribe.Core/Model/Markers.cs tests/LocalScribe.Core.Tests/ChannelMapperDownmixMarkerTests.cs
git commit -m "fix(import): mark a 2-channel downmix, not only >2 channels

ChannelMapPlan.DownmixedMultichannel -> Downmixed, condition > 2 -> > 1.
A stereo two-party call imported without ticking 'each party is on their
own channel' became one mixed mono leg with nothing in the transcript
recording it. Mono still produces no marker - nothing was downmixed.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YLjXhg7YDw7x6EHfGyUuYk"
```

---

### Task 3: Shared sherpa model names + availability probe

**Files:**
- Create: `src/LocalScribe.Core/Diarisation/DiarisationModels.cs`
- Create: `src/LocalScribe.App/Services/DiarisationAvailability.cs`
- Modify: `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs:428-429`
- Modify: `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs:723-724`
- Test: `tests/LocalScribe.App.Tests/DiarisationAvailabilityTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `LocalScribe.Core.Diarisation.DiarisationModels.Segmentation` = `"sherpa-onnx-pyannote-segmentation-3-0/model.onnx"` and `.Embedding` = `"3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"` (both `public const string`).
  - `LocalScribe.App.Services.DiarisationAvailability.Probe(Func<string,string> resolveModel, string exePath)` returning `string?` — a user-facing reason when unavailable, `null` when everything is present. Tasks 5 and 8 both call this.

**Context you need:** the two model filenames are currently inline literals at `SplitSpeakersViewModel.cs:428-429`, and the embedding one is *duplicated* as `SettingsPageViewModel.EmbeddingModelFile` (`:723-724`) with a doc comment warning the twins must stay identical or enrollment `Method` stamps stop matching. This task adds a third consumer, so hoist to one place first.

`ModelPaths.Resolve` is `Path.Combine(ModelsRoot, fileName)` with **no existence check** (`ModelPaths.cs:23`), deliberately. The helper exe path is a bare `Path.Combine(AppContext.BaseDirectory, "LocalScribe.Diarizer.exe")` at `CompositionRoot.cs:134` — there is no `DiarizerHelperLocator`. And a missing exe does **not** surface as `DiarisationException`: `Process.Start` throws `Win32Exception` out of `ProcessDiarisationHelper.cs:33`, and `SherpaHelperDiariser.cs:47` does not catch it, so it propagates raw.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/DiarisationAvailabilityTests.cs`:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Diarisation;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pre-flight availability gate for speaker detection (design 2026-07-28 section 5).
/// LocalScribe.Diarizer.exe is deployed by NO build step (App.csproj:32-38 documents that a
/// same-folder copy would overwrite App's onnxruntime.dll 1.22 with sherpa's 1.24.4), and
/// ModelPaths.Resolve does no existence check (ModelPaths.cs:23), so the gate must probe for
/// itself. Without it, a missing helper surfaces as a raw Win32Exception from
/// ProcessDiarisationHelper.cs:33 AFTER transcription has already burned minutes.</summary>
public sealed class DiarisationAvailabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_avail_{Guid.NewGuid():N}");

    public DiarisationAvailabilityTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Models => Path.Combine(_root, "models");
    private string Resolve(string name) => Path.Combine(Models, name);
    private string Exe => Path.Combine(_root, "LocalScribe.Diarizer.exe");

    private void WriteModel(string name)
    {
        string p = Resolve(name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, [1, 2, 3]);
    }

    private void WriteExe() => File.WriteAllBytes(Exe, [1, 2, 3]);

    [Fact]
    public void Null_when_the_exe_and_both_models_are_present()
    {
        WriteExe();
        WriteModel(DiarisationModels.Segmentation);
        WriteModel(DiarisationModels.Embedding);

        Assert.Null(DiarisationAvailability.Probe(Resolve, Exe));
    }

    [Fact]
    public void Names_the_missing_helper_exe()
    {
        WriteModel(DiarisationModels.Segmentation);
        WriteModel(DiarisationModels.Embedding);

        string? reason = DiarisationAvailability.Probe(Resolve, Exe);
        Assert.NotNull(reason);
        Assert.Contains("LocalScribe.Diarizer.exe", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_a_missing_model()
    {
        WriteExe();
        WriteModel(DiarisationModels.Segmentation);   // embedding model deliberately absent

        string? reason = DiarisationAvailability.Probe(Resolve, Exe);
        Assert.NotNull(reason);
        Assert.Contains("model", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_zero_byte_file_does_not_count_as_present()
    {
        WriteExe();
        WriteModel(DiarisationModels.Segmentation);
        string p = Resolve(DiarisationModels.Embedding);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, []);

        Assert.NotNull(DiarisationAvailability.Probe(Resolve, Exe));
    }

    [Fact]
    public void The_segmentation_name_is_a_subpath_and_survives_Path_Combine()
    {
        // Deliberately a forward-slash subpath (sherpa ships the model inside a folder).
        Assert.Contains('/', DiarisationModels.Segmentation);
        Assert.EndsWith("model.onnx", DiarisationModels.Segmentation, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~DiarisationAvailabilityTests" 2>&1 | tail -5`
Expected: FAIL — neither `DiarisationModels` nor `DiarisationAvailability` exists.

- [ ] **Step 3: Write the implementation**

Create `src/LocalScribe.Core/Diarisation/DiarisationModels.cs`:

```csharp
namespace LocalScribe.Core.Diarisation;

/// <summary>The two sherpa ONNX model filenames every diarisation path resolves through
/// ModelPaths.Resolve. Hoisted here (design 2026-07-28 task 3) from three duplicated literals:
/// SplitSpeakersViewModel.RunAsync, SettingsPageViewModel.EmbeddingModelFile, and the import-time
/// detection step. The embedding name in particular MUST be one value everywhere - an enrollment
/// made under one file is stamped with a Method that can never match one made under another.</summary>
public static class DiarisationModels
{
    /// <summary>Segmentation model. A forward-slash SUBPATH: sherpa ships model.onnx inside a
    /// versioned folder, and Path.Combine handles the separator.</summary>
    public const string Segmentation = "sherpa-onnx-pyannote-segmentation-3-0/model.onnx";

    /// <summary>Speaker-embedding model (CAM++). Flat filename.</summary>
    public const string Embedding = "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx";
}
```

Create `src/LocalScribe.App/Services/DiarisationAvailability.cs`:

```csharp
// src/LocalScribe.App/Services/DiarisationAvailability.cs
using System.IO;
using LocalScribe.Core.Diarisation;

namespace LocalScribe.App.Services;

/// <summary>Pre-flight gate for speaker detection (design 2026-07-28 section 5), mirroring the
/// import model-presence gate at AudioImporter.cs:77-92: refuse visibly and up front rather than
/// crash after minutes of transcription.
///
/// This has to probe for itself. ModelPaths.Resolve is a bare Path.Combine with no existence check
/// (ModelPaths.cs:23, deliberate), ModelPaths.AvailableModels only enumerates ggml-*.bin so sherpa
/// models are invisible to it, and LocalScribe.Diarizer.exe is deployed by no build step at all
/// (App.csproj:32-38 - a same-folder copy would overwrite App's onnxruntime.dll 1.22 with sherpa's
/// 1.24.4 and is "actively unsafe"). A missing exe does NOT surface as DiarisationException:
/// Process.Start throws Win32Exception out of ProcessDiarisationHelper.cs:33 and
/// SherpaHelperDiariser.cs:47 does not catch it.</summary>
public static class DiarisationAvailability
{
    /// <summary>Returns a user-facing reason speaker detection is unavailable, or null when the
    /// helper exe and both sherpa models are present and non-empty.</summary>
    public static string? Probe(Func<string, string> resolveModel, string exePath)
    {
        if (!Present(exePath))
            return "Speaker detection unavailable - LocalScribe.Diarizer.exe is not installed.";
        if (!Present(resolveModel(DiarisationModels.Segmentation)))
            return "Speaker detection unavailable - the speaker segmentation model is not installed.";
        if (!Present(resolveModel(DiarisationModels.Embedding)))
            return "Speaker detection unavailable - the speaker embedding model is not installed.";
        return null;
    }

    // Zero-byte counts as absent: a truncated download is not a usable model, and the publish
    // guards (tools/verify-*.ps1) use the same missing-or-empty test.
    private static bool Present(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;   // unreadable is unavailable; never throw out of a pre-flight probe
        }
    }
}
```

Now de-duplicate the two existing literals. In `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs`, replace lines 428-429:

```csharp
            string segModel = _resolveModel(DiarisationModels.Segmentation);
            string embModel = _resolveModel(DiarisationModels.Embedding);
```

(`LocalScribe.Core.Diarisation` is already in that file's using block at line 8 — no new using needed.)

In `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs`, replace the const at 719-724 with a forwarding const so the "twins must match" hazard disappears:

```csharp
    /// <summary>The embedding model the backfill scan runs. Now a single source of truth with the
    /// diarise path (design 2026-07-28 task 3): both resolve DiarisationModels.Embedding, so an
    /// enrollment made here can never be stamped with a Method that fails to match one made in
    /// SplitSpeakersViewModel's confirm path.</summary>
    private const string EmbeddingModelFile = LocalScribe.Core.Diarisation.DiarisationModels.Embedding;
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~DiarisationAvailabilityTests" 2>&1 | tail -5`
Expected: PASS, 5 tests.

- [ ] **Step 5: Verify the de-duplication broke nothing**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SplitSpeakers" 2>&1 | tail -5`
Expected: PASS.

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~Settings" 2>&1 | tail -5`
Expected: PASS.

Confirm the literals are gone from both VMs:

Run: `grep -rn "3dspeaker_speech_campplus\|pyannote-segmentation" src/ --include=*.cs`
Expected: exactly two hits, both in `src/LocalScribe.Core/Diarisation/DiarisationModels.cs`.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Diarisation/DiarisationModels.cs src/LocalScribe.App/Services/DiarisationAvailability.cs src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs tests/LocalScribe.App.Tests/DiarisationAvailabilityTests.cs
git commit -m "feat(diarisation): hoist sherpa model names + add a pre-flight availability probe

The embedding filename was duplicated across SplitSpeakersViewModel and
SettingsPageViewModel with a comment warning the twins must stay identical
or enrollment Method stamps stop matching. Import-time detection would have
been a third copy; now there is one.

DiarisationAvailability.Probe exists because a missing helper does NOT
surface as DiarisationException - Process.Start throws Win32Exception out
of ProcessDiarisationHelper.cs:33 and SherpaHelperDiariser.cs:47 does not
catch it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YLjXhg7YDw7x6EHfGyUuYk"
```

---

### Task 4: `MaintenanceService.RenameSpeakersAsync` — a names-only write path

**Files:**
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs` (add after `SaveDiarisationAsync`, i.e. after line 569)
- Test: `tests/LocalScribe.App.Tests/MaintenanceServiceRenameSpeakersTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces:
  ```csharp
  public Task<bool> RenameSpeakersAsync(
      string sessionId, string versionId,
      IReadOnlyDictionary<string, string> names,
      IReadOnlyDictionary<string, string>? participantClusterKeys,
      IReadOnlyDictionary<string, SuggestionProvenanceEntry>? provenance,
      CancellationToken ct)
  ```
  Task 7 (Split Speakers hydration) calls exactly this.

**Why this exists — read before implementing.** The spec's open item asked whether a hydrated rename could reuse `SaveDiarisationAsync`. It cannot, and this is the single most important judgement in the plan.

`SaveDiarisationAsync` routes through `SpeakersMerge.Merge(existing, commit, owned)`, whose entire job is to protect *pinned and owned* clusterKeys from a **fresh** run by remapping colliding fresh keys to unused ids (`SpeakersMerge.cs` doc at `:8-19`). On a rename the "fresh" keys **are** the existing keys, so a key that is both pinned and present in the reconstructed commit would collide with *itself* and be remapped away — silently duplicating the cluster under a new id. Beyond that, `SaveDiarisationAsync` restamps `Method`/`DiarisedAtUtc`, re-derives `embeddings.json` (only `resultsBySource: null` leaves it alone), and flips `Diarised`. None of that is true of typing a name.

So a rename gets its own narrow path that touches **only** `Names`, `SuggestionProvenance`, participant `ClusterKey` ownership, and the projections.

**Context you need:** `RunForSessionAsync<T>` is a single generic method (`:61-70`), no overloads — void-ish callers return `true`. `EnsureKnownVersion` (`:139-154`) validates the caller-supplied `versionId` under the same gate hold; never re-resolve `ActiveVersion` at write time. `SessionContentChanged` is raised **outside** the gate and never on a no-op (`SaveDiarisationAsync` is the deliberate exception). `MetadataStore.SaveAsync` is a **full overwrite**. This path must never flip `meta.Edited`/`LastEditedAtUtc` — those are reserved for manual corrections.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/MaintenanceServiceRenameSpeakersTests.cs`:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>RenameSpeakersAsync (design 2026-07-28 task 4): the names-only write path used when a
/// user renames an ALREADY-committed diarisation, so reopening Split Speakers to type a name never
/// re-runs the diariser.
///
/// It deliberately does NOT go through SaveDiarisationAsync/SpeakersMerge. Merge's job is to protect
/// pinned/owned keys from a FRESH run by remapping colliding fresh keys; on a rename the "fresh"
/// keys ARE the existing keys, so a pinned key present in the commit would collide with itself and
/// be remapped away - duplicating the cluster. A rename also must not restamp Method/DiarisedAtUtc,
/// re-derive embeddings.json, or flip Diarised.</summary>
public sealed class MaintenanceServiceRenameSpeakersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_rename_{Guid.NewGuid():N}");

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private (MaintenanceService svc, StoragePaths paths, string id) MakeDiarisedSession(
        IReadOnlyList<SessionParticipant>? participants = null)
    {
        var paths = new StoragePaths(_root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));

        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = [SourceKind.Local],
            Diarised = true,
        }, default).GetAwaiter().GetResult();

        new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            LocalCount = 2,
            Participants = participants ?? [],
        }, default).GetAwaiter().GetResult();

        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id));
        jsonl.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 0, 1000, "hi", "Me"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Local, 1000, 2000, "there", "Me"), default).GetAwaiter().GetResult();

        // An already-committed diarisation: two clusters, default labels, one pinned seq.
        new SpeakersStore(paths.SpeakersJson(id)).SaveAsync(new Speakers
        {
            Names = new Dictionary<string, string>
            { ["Local:0"] = "Local Speaker 1", ["Local:1"] = "Local Speaker 2" },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Local"] = new() { ["1"] = "Local:0", ["2"] = "Local:1" } },
            Pinned = new Dictionary<string, List<string>> { ["Local"] = ["1"] },
            DiarisedSources = [SourceKind.Local],
            Method = "sherpa",
            DiarisedAtUtc = DateTimeOffset.UnixEpoch,
        }, default).GetAwaiter().GetResult();

        var svc = new MaintenanceService(paths, new FakeSettingsService(new Settings()),
            new FakeRecycleBin(), TimeProvider.System);
        return (svc, paths, id);
    }

    [Fact]
    public async Task Renames_without_disturbing_assignments_pins_or_the_diarisation_stamp()
    {
        var (svc, paths, id) = MakeDiarisedSession();

        bool wrote = await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Sarah Chen", ["Local:1"] = "Tom Ridge" },
            participantClusterKeys: null, provenance: null, default);

        Assert.True(wrote);
        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);

        Assert.Equal("Sarah Chen", s!.Names["Local:0"]);
        Assert.Equal("Tom Ridge", s.Names["Local:1"]);

        // Everything a rename must NOT touch:
        Assert.Equal("Local:0", s.Assignments["Local"]["1"]);
        Assert.Equal("Local:1", s.Assignments["Local"]["2"]);
        Assert.Equal(["1"], s.Pinned["Local"]);
        Assert.Equal("sherpa", s.Method);
        Assert.Equal(DateTimeOffset.UnixEpoch, s.DiarisedAtUtc);
        Assert.Contains(SourceKind.Local, s.DiarisedSources);
    }

    [Fact]
    public async Task A_pinned_cluster_key_is_never_remapped_or_duplicated()
    {
        // THE regression this method exists for: routing a rename through
        // SaveDiarisationAsync/SpeakersMerge would see "Local:0" as a fresh key colliding with the
        // pinned "Local:0" and remap it to an unused id, leaving two rows for one voice.
        var (svc, paths, id) = MakeDiarisedSession();

        await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Sarah Chen", ["Local:1"] = "Tom Ridge" },
            participantClusterKeys: null, provenance: null, default);

        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal(2, s!.Names.Count);
        Assert.DoesNotContain("Local:2", s.Names.Keys);
    }

    [Fact]
    public async Task Leaves_the_embeddings_sidecar_completely_alone()
    {
        var (svc, paths, id) = MakeDiarisedSession();
        var embPath = paths.EmbeddingsJson(id, "v1");
        Directory.CreateDirectory(Path.GetDirectoryName(embPath)!);
        await new ClusterEmbeddingsStore(embPath).SaveAsync(new ClusterEmbeddings
        {
            Method = "campplus",
            ExtractedAtUtc = DateTimeOffset.UnixEpoch,
            Entries = new Dictionary<string, float[]> { ["Local:0"] = [1f, 0f], ["Local:1"] = [0f, 1f] },
        }, default);
        var before = await File.ReadAllTextAsync(embPath);

        await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Sarah Chen" },
            participantClusterKeys: null, provenance: null, default);

        Assert.Equal(before, await File.ReadAllTextAsync(embPath));
    }

    [Fact]
    public async Task Persists_participant_ownership_without_flipping_the_edited_flag()
    {
        var (svc, paths, id) = MakeDiarisedSession(
            [new SessionParticipant { Id = "p1", Name = "Sarah Chen", Side = SourceKind.Local, Kind = ParticipantKind.Named }]);

        await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Sarah Chen" },
            new Dictionary<string, string> { ["p1"] = "Local:0" },
            provenance: null, default);

        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal("Local:0", meta!.Participants.Single(p => p.Id == "p1").ClusterKey);
        // Edited/LastEditedAtUtc are reserved for manual transcript corrections.
        Assert.False(meta.Edited);
        Assert.Null(meta.LastEditedAtUtc);
    }

    [Fact]
    public async Task Records_accepted_suggestion_provenance()
    {
        var (svc, paths, id) = MakeDiarisedSession();

        await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Sarah Chen" },
            participantClusterKeys: null,
            new Dictionary<string, SuggestionProvenanceEntry>
            { ["Local:0"] = new("person-1", 0.87, DateTimeOffset.UnixEpoch) },
            default);

        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("person-1", s!.SuggestionProvenance["Local:0"].PersonId);
    }

    [Fact]
    public async Task Regenerates_projections_with_the_new_names()
    {
        var (svc, paths, id) = MakeDiarisedSession();

        await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Sarah Chen", ["Local:1"] = "Tom Ridge" },
            participantClusterKeys: null, provenance: null, default);

        string txt = await File.ReadAllTextAsync(paths.TranscriptTxt(id));
        Assert.Contains("Sarah Chen", txt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Returns_false_when_the_session_has_no_speakers_overlay_to_rename()
    {
        var paths = new StoragePaths(_root);
        string id = "empty";
        Directory.CreateDirectory(paths.SessionDir(id));
        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
        }, default);
        var svc = new MaintenanceService(paths, new FakeSettingsService(new Settings()),
            new FakeRecycleBin(), TimeProvider.System);

        Assert.False(await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Nobody" },
            participantClusterKeys: null, provenance: null, default));
    }

    [Fact]
    public async Task Rejects_a_version_the_session_never_recorded()
    {
        var (svc, _, id) = MakeDiarisedSession();

        await Assert.ThrowsAsync<ArgumentException>(() => svc.RenameSpeakersAsync(
            id, "v99", new Dictionary<string, string> { ["Local:0"] = "X" },
            participantClusterKeys: null, provenance: null, default));
    }
}
```

> **Before running:** confirm the exact spellings of `StoragePaths.EmbeddingsJson(id, versionId)`, `ClusterEmbeddingsStore`, `ClusterEmbeddings`, `SessionParticipant`'s init members (`Id`, `Name`, `Side`, `Kind`, `ClusterKey`) and `ParticipantKind.Named` with
> `grep -n "EmbeddingsJson\|class ClusterEmbeddingsStore\|record ClusterEmbeddings\|record SessionParticipant\|enum ParticipantKind" -r src/LocalScribe.Core`
> and adjust the fixture to match. Also confirm `SpeakersJson(id)` has a one-arg overload defaulting to root — `MaintenanceServiceDiarisationTests.cs:70` uses `paths.SpeakersJson(id)` while the service uses `paths.SpeakersJson(sessionId, versionId)`. Note the tests above pass `"v1"` as the versionId; if root is spelled differently in this repo (`TranscriptVersions.Root`), use that constant instead and drop the `Rejects_a_version...` test's assumption accordingly.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceServiceRenameSpeakersTests" 2>&1 | tail -5`
Expected: FAIL — `RenameSpeakersAsync` does not exist.

- [ ] **Step 3: Write the implementation**

In `src/LocalScribe.App/Services/MaintenanceService.cs`, immediately after the closing brace of the primary `SaveDiarisationAsync` (line 569), add:

```csharp
    /// <summary>Rename already-committed diarisation clusters (design 2026-07-28 task 4): writes
    /// ONLY speakers.json Names + SuggestionProvenance, participant ClusterKey ownership, and the
    /// projections. Used when a user reopens Split Speakers on a diarised session and types a name,
    /// so renaming never costs a second diarisation run.
    ///
    /// Deliberately NOT routed through SaveDiarisationAsync/SpeakersMerge. Merge exists to protect
    /// pinned and participant-owned clusterKeys from a FRESH run by remapping colliding fresh keys
    /// to unused ids; on a rename the "fresh" keys ARE the existing keys, so a pinned key present in
    /// the commit would collide with itself and be remapped away, duplicating one voice across two
    /// rows. A rename also must not restamp Method/DiarisedAtUtc, must not re-derive embeddings.json
    /// (the vectors describe the run, not the label), and must not flip Diarised (already true).
    ///
    /// <paramref name="names"/> is clusterKey -> display name; keys absent from the existing overlay
    /// are ignored rather than invented. <paramref name="participantClusterKeys"/> maps participant
    /// Id -> clusterKey; no remap translation is needed or applied because these keys already
    /// landed in speakers.json. <paramref name="provenance"/> is merged in the same shape.
    /// Never flips meta.Edited/LastEditedAtUtc (reserved for manual corrections).
    /// <paramref name="versionId"/> is validated against the session's recorded versions and is
    /// never re-resolved from disk (the F1 fix - see EnsureKnownVersion).
    /// Returns false (writing nothing) when the session or its speakers overlay is absent.</summary>
    public async Task<bool> RenameSpeakersAsync(string sessionId, string versionId,
        IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, string>? participantClusterKeys,
        IReadOnlyDictionary<string, SuggestionProvenanceEntry>? provenance,
        CancellationToken ct)
    {
        bool wrote = await RunForSessionAsync(sessionId, async inner =>
        {
            var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(inner);
            if (session is null) return false;
            EnsureKnownVersion(sessionId, versionId, session);

            var store = new SpeakersStore(paths.SpeakersJson(sessionId, versionId));
            var existing = await store.LoadAsync(inner);
            if (existing is null) return false;      // nothing committed here to rename

            // Only rename keys the overlay already knows: a stale VM row must never invent a
            // cluster that no assignment points at.
            var mergedNames = new Dictionary<string, string>(existing.Names, StringComparer.Ordinal);
            bool changed = false;
            foreach (var (key, name) in names)
            {
                if (!mergedNames.ContainsKey(key)) continue;
                if (string.Equals(mergedNames[key], name, StringComparison.Ordinal)) continue;
                mergedNames[key] = name;
                changed = true;
            }

            var mergedProvenance = new Dictionary<string, SuggestionProvenanceEntry>(
                existing.SuggestionProvenance, StringComparer.Ordinal);
            if (provenance is not null)
                foreach (var (key, entry) in provenance)
                {
                    if (!mergedNames.ContainsKey(key)) continue;
                    mergedProvenance[key] = entry;
                    changed = true;
                }

            if (changed)
                await store.SaveAsync(
                    existing with { Names = mergedNames, SuggestionProvenance = mergedProvenance }, inner);

            // Participant ClusterKey ownership. No FreshKeyRemap translation: these keys are the
            // ones already on disk, not pre-merge fresh keys.
            if (participantClusterKeys is { Count: > 0 })
            {
                var metaStore = new MetadataStore(paths.MetaJson(sessionId));
                var meta = await metaStore.LoadAsync(inner);
                if (meta is not null)
                {
                    var updated = meta.Participants
                        .Select(p => participantClusterKeys.TryGetValue(p.Id, out var key)
                            ? p with { ClusterKey = key }
                            : p)
                        .ToList();
                    if (!updated.SequenceEqual(meta.Participants))   // records: value equality
                    {
                        await metaStore.SaveAsync(meta with { Participants = updated }, inner);
                        changed = true;
                    }
                }
            }

            if (!changed) return false;

            await new SessionWriter(paths, settings.Current, time)
                .RegenerateProjectionsAsync(sessionId, inner);
            return true;
        }, ct);

        if (wrote) RaiseSessionContentChanged(sessionId);   // names feed the search index + read view
        return wrote;
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceServiceRenameSpeakersTests" 2>&1 | tail -5`
Expected: PASS, 8 tests.

- [ ] **Step 5: Confirm the diarisation write path is untouched**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceServiceDiarisationTests" 2>&1 | tail -5`
Expected: PASS — including the `[Theory]` over `keep`/`never`/`days:30` that pins the no-audio-deletion firewall.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/MaintenanceServiceRenameSpeakersTests.cs
git commit -m "feat(speakers): RenameSpeakersAsync - a names-only write path

Renaming an already-committed diarisation must not go through
SaveDiarisationAsync. SpeakersMerge protects pinned/owned keys from a
FRESH run by remapping colliding fresh keys; on a rename the fresh keys
ARE the existing keys, so a pinned key would collide with itself and be
remapped away, duplicating one voice across two rows. A rename also must
not restamp Method/DiarisedAtUtc or re-derive embeddings.json.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YLjXhg7YDw7x6EHfGyUuYk"
```

---

### Task 5: `SpeakerDetectionStep` — the post-import detection phase

**Files:**
- Create: `src/LocalScribe.App/Services/AudioLegProbe.cs`
- Create: `src/LocalScribe.App/Services/SpeakerDetectionStep.cs`
- Modify: `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs:359-370` (delegate `ProbeLeg` to the shared probe)
- Modify: `src/LocalScribe.Core/Model/Markers.cs` (three new marker templates)
- Test: `tests/LocalScribe.App.Tests/SpeakerDetectionStepTests.cs` (create)

**Interfaces:**
- Consumes: `SpeakerDetection` (Task 1), `DiarisationModels` + `DiarisationAvailability` (Task 3).
- Produces:
  ```csharp
  public enum SpeakerDetectionResult { Committed, OneVoice, NoAudio, Unavailable, Failed, Cancelled }
  public sealed record SpeakerDetectionOutcome(SpeakerDetectionResult Result, int ClusterCount);

  public sealed class SpeakerDetectionStep(
      IDiarisationEngine engine, MaintenanceService maintenance, StoragePaths paths,
      ISettingsService settings, Func<string, string> resolveModel, string diarizerExePath,
      TimeProvider time)
  {
      public Task<SpeakerDetectionOutcome> RunAsync(string sessionId, SpeakerDetection mode,
          int? speakerCount, IProgress<double>? progress, CancellationToken ct);
  }

  public static class AudioLegProbe
  {
      public static string? Resolve(StoragePaths paths, string sessionId, SourceKind kind,
          IReadOnlyList<SourceKind> retained, AudioFormat preferredFormat);
  }
  ```
  Task 9 constructs `SpeakerDetectionStep` and routes on `SpeakerDetectionResult`.

**Context you need — five constraints that shape the body:**

1. **Never hold the per-session gate across the diarise call.** `RunForSessionAsync` is a `SemaphoreSlim(1,1)` per session (`MaintenanceService.cs:61-70`) and diarisation is minutes of CPU. Read under the gate, diarise **outside** it, then commit (`SaveDiarisationAsync` takes the gate itself).
2. **A missing helper throws `Win32Exception`, not `DiarisationException`.** `Process.Start` throws it out of `ProcessDiarisationHelper.cs:33` and `SherpaHelperDiariser.cs:47` does not catch it. So the failure path must catch `Exception` broadly, ordered after `OperationCanceledException`.
3. **`AudioRetention = "never"` means no FLAC leg exists.** `OfflinePipelineRunner.cs:193-204` only retains legs when retention is not `"never"`, so detection has nothing to read. That is the `NoAudio` outcome, and it gets a marker — silent absence would be indistinguishable from "detection was never asked for".
4. **A marker appended after the Save stage makes `MarkerCount` stale.** `AudioImporter.cs:181-200` recounts markers into session.json during Save; anything appended afterwards is not counted. Every marker this step writes must append **and** correct `MarkerCount`, under the gate.
5. **`meta.LocalCount` must be written truthfully and must not flip `Edited`.** `Declared(n)` → `n` (the user asserted it, and it drives the force-N button on a manual retry, so it is written even on the failure paths). `Auto` → the number of clusters actually committed, only when ≥ 2.

- [ ] **Step 1: Add the three markers**

In `src/LocalScribe.Core/Model/Markers.cs`, after `ImportedDownmixed` (line 49), add:

```csharp

    // Import-time speaker detection (design 2026-07-28 section 5). Only the outcomes that leave no
    // other trace are marked: on success speakers.json + SessionRecord.Diarised ARE the record, so
    // a marker would be redundant clutter. {0} in SpeakerDetectionFailed is the failure detail.
    public const string SpeakerDetectionFailed =
        "speaker detection did not complete: {0}. The transcript and audio are unaffected.";
    public const string SpeakerDetectionOneVoice =
        "speaker detection found only one voice; no speaker labels were applied.";
    public const string SpeakerDetectionNoAudio =
        "speaker detection could not run: no retained audio leg for this session.";
```

- [ ] **Step 2: Write the failing test**

Create `tests/LocalScribe.App.Tests/SpeakerDetectionStepTests.cs`:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Import;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The post-import speaker-detection phase (design 2026-07-28 section 3). Runs AFTER
/// AudioImporter.ImportAsync has returned, so a diariser failure can never reach the
/// Directory.Delete-the-whole-session catch at AudioImporter.cs:205-210, and the Diarised flag it
/// commits is not clobbered by the Save-stage snapshot window at AudioImporter.cs:183-200.</summary>
public sealed class SpeakerDetectionStepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_sds_{Guid.NewGuid():N}");

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeEngine : IDiarisationEngine
    {
        public int Calls { get; private set; }
        public int? LastForced { get; private set; }
        public bool? LastEmitEmbeddings { get; private set; }
        public string? LastFlacPath { get; private set; }
        public Exception? Throw { get; set; }
        public DiarisationResult Next { get; set; } = new(
            [new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)], 2, "sherpa");

        public Task<DiarisationResult> DiariseAsync(
            DiarisationRequest r, IProgress<double> p, CancellationToken ct)
        {
            Calls++;
            LastForced = r.ForcedClusterCount;
            LastEmitEmbeddings = r.EmitEmbeddings;
            LastFlacPath = r.FlacPath;
            if (Throw is not null) throw Throw;
            p.Report(0.5);
            p.Report(1.0);
            return Task.FromResult(Next);
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>An imported, finalized, NOT-yet-diarised session with one retained Local leg and
    /// two Local segments - exactly the shape AudioImporter leaves behind for a mono import
    /// (SessionMeta.LocalCount stays at its default 1; imports never raise it).</summary>
    private (SpeakerDetectionStep step, StoragePaths paths, string id, FakeEngine engine)
        MakeImportedSession(bool retainAudio = true, string audioRetention = "keep")
    {
        var paths = new StoragePaths(_root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));

        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            Origin = "imported",
            RetainedAudioSources = retainAudio ? [SourceKind.Local] : [],
            MarkerCount = 0,
        }, default).GetAwaiter().GetResult();

        new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta(), default).GetAwaiter().GetResult();

        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id));
        jsonl.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "hi", "Me"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 1000, 2000, "there", "Me"), default).GetAwaiter().GetResult();

        if (retainAudio)
            File.WriteAllBytes(paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac), [1, 2, 3]);

        // Both sherpa models + the helper exe present, so the availability gate passes.
        string models = Path.Combine(_root, "models");
        foreach (var name in new[] { DiarisationModels.Segmentation, DiarisationModels.Embedding })
        {
            string p = Path.Combine(models, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, [1, 2, 3]);
        }
        string exe = Path.Combine(_root, "LocalScribe.Diarizer.exe");
        File.WriteAllBytes(exe, [1, 2, 3]);

        var settings = new FakeSettingsService(new Settings { AudioRetention = audioRetention });
        var maintenance = new MaintenanceService(paths, settings, new FakeRecycleBin(), TimeProvider.System);
        var engine = new FakeEngine();
        var step = new SpeakerDetectionStep(engine, maintenance, paths, settings,
            name => Path.Combine(models, name.Replace('/', Path.DirectorySeparatorChar)), exe,
            TimeProvider.System);
        return (step, paths, id, engine);
    }

    private static async Task<IReadOnlyList<string>> MarkerTextsAsync(StoragePaths paths, string id)
    {
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id)).ReadAllAsync(default);
        return lines.Where(l => l.Kind == TranscriptKind.Marker).Select(l => l.Text).ToList();
    }

    [Fact]
    public async Task Auto_commits_default_labels_and_flips_diarised()
    {
        var (step, paths, id, engine) = MakeImportedSession();

        var outcome = await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(SpeakerDetectionResult.Committed, outcome.Result);
        Assert.Equal(2, outcome.ClusterCount);
        Assert.Null(engine.LastForced);                 // auto == ForcedClusterCount null
        Assert.True(engine.LastEmitEmbeddings);         // so the voiceprint chips work on reopen

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Local Speaker 1", speakers!.Names["Local:0"]);
        Assert.Equal("Local Speaker 2", speakers.Names["Local:1"]);
        Assert.Equal("Local:0", speakers.Assignments["Local"]["0"]);

        var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
        Assert.True(session!.Diarised);

        // Success leaves no marker: speakers.json + Diarised ARE the record.
        Assert.Empty(await MarkerTextsAsync(paths, id));
    }

    [Fact]
    public async Task Declared_forces_the_count_and_writes_it_to_meta()
    {
        var (step, paths, id, engine) = MakeImportedSession();

        await step.RunAsync(id, SpeakerDetection.Declared, 3, null, default);

        Assert.Equal(3, engine.LastForced);
        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal(3, meta!.LocalCount);
    }

    [Fact]
    public async Task Auto_writes_the_committed_cluster_count_to_meta()
    {
        var (step, paths, id, _) = MakeImportedSession();

        await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal(2, meta!.LocalCount);   // imports leave this at 1; detection makes it truthful
        Assert.False(meta.Edited);           // never flip the manual-correction flag
    }

    [Fact]
    public async Task One_voice_commits_nothing_and_markers()
    {
        // The untuned 0.5f threshold (SherpaDiarisationRunner.cs:26) collapsed to ONE cluster on the
        // only run on record. Labelling a whole call "Local Speaker 1" is not an improvement over
        // "Me", and since SaveDiarisationAsync never runs, Diarised stays false - so without this
        // marker nothing would record that detection happened at all.
        var (step, paths, id, engine) = MakeImportedSession();
        engine.Next = new DiarisationResult([new DiarisedSegment(0, 2000, 0)], 1, "sherpa");

        var outcome = await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(SpeakerDetectionResult.OneVoice, outcome.Result);
        Assert.Null(await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default));
        var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
        Assert.False(session!.Diarised);
        Assert.Contains(Markers.SpeakerDetectionOneVoice, await MarkerTextsAsync(paths, id));
    }

    [Fact]
    public async Task A_thrown_engine_leaves_the_session_intact_and_markers()
    {
        var (step, paths, id, engine) = MakeImportedSession();
        engine.Throw = new DiarisationException(DiarisationErrorCode.HelperCrash, "boom");

        var outcome = await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(SpeakerDetectionResult.Failed, outcome.Result);
        // The import itself is untouched: folder, transcript segments and audio all still there.
        Assert.True(Directory.Exists(paths.SessionDir(id)));
        Assert.True(File.Exists(paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac)));
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id)).ReadAllAsync(default);
        Assert.Equal(2, lines.Count(l => l.Kind == TranscriptKind.Segment));
        Assert.Contains(await MarkerTextsAsync(paths, id),
            t => t.StartsWith("speaker detection did not complete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_helper_exe_is_caught_even_though_it_throws_Win32Exception()
    {
        // Process.Start throws Win32Exception out of ProcessDiarisationHelper.cs:33 and
        // SherpaHelperDiariser.cs:47 does not catch it, so it propagates RAW - not as a
        // DiarisationException. Catching only DiarisationException would let it escape.
        var (step, paths, id, engine) = MakeImportedSession();
        engine.Throw = new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified.");

        var outcome = await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(SpeakerDetectionResult.Failed, outcome.Result);
        Assert.True(Directory.Exists(paths.SessionDir(id)));
    }

    [Fact]
    public async Task Cancellation_keeps_the_import_and_writes_no_marker()
    {
        var (step, paths, id, engine) = MakeImportedSession();
        engine.Throw = new OperationCanceledException();

        var outcome = await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(SpeakerDetectionResult.Cancelled, outcome.Result);
        Assert.True(Directory.Exists(paths.SessionDir(id)));
        // Cancelling is a choice, not a degradation - nothing to record.
        Assert.Empty(await MarkerTextsAsync(paths, id));
    }

    [Fact]
    public async Task No_retained_leg_reports_NoAudio_without_calling_the_engine()
    {
        var (step, paths, id, engine) = MakeImportedSession(retainAudio: false, audioRetention: "never");

        var outcome = await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(SpeakerDetectionResult.NoAudio, outcome.Result);
        Assert.Equal(0, engine.Calls);
        Assert.Contains(Markers.SpeakerDetectionNoAudio, await MarkerTextsAsync(paths, id));
    }

    [Fact]
    public async Task Every_marker_it_writes_corrects_MarkerCount()
    {
        // AudioImporter.cs:185-200 recounts markers into session.json during the Save stage;
        // anything appended AFTER that is not counted. Detection runs after Save.
        var (step, paths, id, engine) = MakeImportedSession();
        engine.Next = new DiarisationResult([new DiarisedSegment(0, 2000, 0)], 1, "sherpa");

        await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal(1, session!.MarkerCount);
    }

    [Fact]
    public async Task Declared_still_writes_the_count_when_detection_fails()
    {
        // The user asserted it, and it pre-configures the force-N button for a manual retry.
        var (step, paths, id, engine) = MakeImportedSession();
        engine.Throw = new DiarisationException(DiarisationErrorCode.HelperCrash, "boom");

        await step.RunAsync(id, SpeakerDetection.Declared, 4, null, default);

        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal(4, meta!.LocalCount);
    }

    [Fact]
    public async Task Reports_determinate_progress_from_the_helper()
    {
        var (step, paths, id, _) = MakeImportedSession();
        var seen = new List<double>();

        await step.RunAsync(id, SpeakerDetection.Auto, null,
            new SynchronousProgress<double>(seen.Add), default);

        Assert.NotEmpty(seen);
        Assert.Equal(1.0, seen[^1]);
    }

    [Fact]
    public async Task Points_the_engine_at_the_retained_leg()
    {
        var (step, paths, id, engine) = MakeImportedSession();

        await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac), engine.LastFlacPath);
    }

    [Fact]
    public async Task Off_is_a_programming_error()
    {
        var (step, _, id, _) = MakeImportedSession();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            step.RunAsync(id, SpeakerDetection.Off, null, null, default));
    }
}
```

> **Before running:** confirm `SessionRecord.MarkerCount` and `SessionRecord.Origin` exist with those exact names via `grep -n "MarkerCount\|Origin" src/LocalScribe.Core/Model/SessionRecord.cs`, and confirm `DiarisationErrorCode.HelperCrash` and the `DiarisationException(code, detail)` ctor shape via `cat src/LocalScribe.Core/Diarisation/DiarisationException.cs`. Adjust the fixture to match.

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SpeakerDetectionStepTests" 2>&1 | tail -5`
Expected: FAIL — `SpeakerDetectionStep` does not exist.

- [ ] **Step 4: Write the shared leg probe**

Create `src/LocalScribe.App/Services/AudioLegProbe.cs`:

```csharp
// src/LocalScribe.App/Services/AudioLegProbe.cs
using System.IO;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Services;

/// <summary>Resolves a session's retained audio leg on disk. Extracted from
/// SplitSpeakersViewModel.ProbeLeg (design 2026-07-28 task 5) so the import-time detection step
/// points the diariser at EXACTLY the same file the manual dialog does - there is no Origin branch
/// anywhere, imported and recorded sessions resolve identically. Mirrors PlaybackViewModel.Resolve:
/// retained-list check, then the preferred on-disk format, then the other, so a session recorded
/// before a format change still resolves.</summary>
public static class AudioLegProbe
{
    public static string? Resolve(StoragePaths paths, string sessionId, SourceKind kind,
        IReadOnlyList<SourceKind> retained, AudioFormat preferredFormat)
    {
        if (!retained.Contains(kind)) return null;
        string preferred = paths.AudioFile(sessionId, kind, preferredFormat);
        if (File.Exists(preferred)) return preferred;
        var other = preferredFormat == AudioFormat.Flac ? AudioFormat.Wav : AudioFormat.Flac;
        string alternate = paths.AudioFile(sessionId, kind, other);
        return File.Exists(alternate) ? alternate : null;
    }
}
```

Then in `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs`, replace the body at `:359-370` with a delegation (keep the private method so its call sites are unchanged):

```csharp
    // Shared with the import-time detection step (design 2026-07-28): both must point the diariser
    // at the same file, so the probe lives in AudioLegProbe rather than being duplicated.
    private string? ProbeLeg(string sessionId, SourceKind kind,
        IReadOnlyList<SourceKind> retained, AudioFormat preferredFormat)
        => AudioLegProbe.Resolve(_paths, sessionId, kind, retained, preferredFormat);
```

- [ ] **Step 5: Write the detection step**

Create `src/LocalScribe.App/Services/SpeakerDetectionStep.cs`:

```csharp
// src/LocalScribe.App/Services/SpeakerDetectionStep.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Import;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Services;

/// <summary>How one import-time detection pass ended.</summary>
public enum SpeakerDetectionResult
{
    /// <summary>Two or more clusters were committed to speakers.json.</summary>
    Committed,
    /// <summary>Exactly one (or zero) voices found - nothing committed, marker written.</summary>
    OneVoice,
    /// <summary>No retained audio leg to read (AudioRetention "never", or the leg is gone).</summary>
    NoAudio,
    /// <summary>The helper exe or a sherpa model was missing at run time.</summary>
    Unavailable,
    /// <summary>The pass threw. The import itself is untouched and still valid.</summary>
    Failed,
    /// <summary>The user cancelled. The import is kept; nothing is recorded.</summary>
    Cancelled,
}

public sealed record SpeakerDetectionOutcome(SpeakerDetectionResult Result, int ClusterCount);

/// <summary>The post-import speaker-detection phase (design 2026-07-28 section 3). Deliberately
/// runs AFTER AudioImporter.ImportAsync has returned, in the App layer:
///
/// - AudioImporter.cs:205-210 deletes the ENTIRE session folder on any throw inside its try. A
///   DiarisationException raised in there would destroy a fully transcribed, fully provenanced
///   import. Running afterwards makes that structurally impossible.
/// - The Save-stage `record with` at AudioImporter.cs:185 operates on a snapshot read at :183, so
///   anything writing session.json in between is clobbered - including Diarised.
/// - MaintenanceService lives in the WPF assembly, so Core could not call the commit path anyway.
///
/// The diarise call runs OUTSIDE the per-session gate: it is minutes of CPU and
/// RunForSessionAsync is a SemaphoreSlim(1,1) that every other writer for this session queues on.
/// Reads happen under the gate, the engine runs outside it, and SaveDiarisationAsync takes the gate
/// itself.</summary>
public sealed class SpeakerDetectionStep(
    IDiarisationEngine engine,
    MaintenanceService maintenance,
    StoragePaths paths,
    ISettingsService settings,
    Func<string, string> resolveModel,
    string diarizerExePath,
    TimeProvider time)
{
    public async Task<SpeakerDetectionOutcome> RunAsync(string sessionId, SpeakerDetection mode,
        int? speakerCount, IProgress<double>? progress, CancellationToken ct)
    {
        if (mode == SpeakerDetection.Off)
            throw new ArgumentOutOfRangeException(nameof(mode),
                "SpeakerDetectionStep must not be invoked for SpeakerDetection.Off.");

        int? forced = mode == SpeakerDetection.Declared ? speakerCount : null;

        try
        {
            // Re-check availability: the dialog gated at open, but the exe could have gone in the
            // interval, and a missing one throws Win32Exception rather than DiarisationException.
            if (DiarisationAvailability.Probe(resolveModel, diarizerExePath) is string unavailable)
            {
                await MarkAsync(sessionId, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    Markers.SpeakerDetectionFailed, unavailable), ct);
                await WriteDeclaredCountAsync(sessionId, mode, speakerCount, ct);
                return new SpeakerDetectionOutcome(SpeakerDetectionResult.Unavailable, 0);
            }

            // --- read phase, under the gate ---
            var loaded = await maintenance.RunForSessionAsync(sessionId, async inner =>
            {
                var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(inner)
                    ?? throw new InvalidOperationException($"session.json missing for {sessionId}");
                var lines = await new TranscriptStore(
                    paths.TranscriptJsonl(sessionId, session.ActiveVersion)).ReadAllAsync(inner);
                string? leg = AudioLegProbe.Resolve(paths, sessionId, SourceKind.Local,
                    session.RetainedAudioSources, settings.Current.AudioFormat);
                return (session.ActiveVersion, lines, leg);
            }, ct);

            if (loaded.leg is null)
            {
                await MarkAsync(sessionId, Markers.SpeakerDetectionNoAudio, ct);
                await WriteDeclaredCountAsync(sessionId, mode, speakerCount, ct);
                return new SpeakerDetectionOutcome(SpeakerDetectionResult.NoAudio, 0);
            }

            // --- diarise, OUTSIDE the gate ---
            var request = new DiarisationRequest(
                loaded.leg, SourceKind.Local,
                resolveModel(DiarisationModels.Segmentation),
                resolveModel(DiarisationModels.Embedding),
                forced,
                // Emit embeddings so embeddings.json lands during the import and the voiceprint
                // suggestion chips work when Split Speakers opens - without a second pass.
                EmitEmbeddings: true);

            var result = await engine.DiariseAsync(request, progress ?? NullProgress.Instance, ct);
            var assignment = ClusterAssigner.Assign(loaded.lines, result.Segments, SourceKind.Local);

            // A collapse to one cluster is exactly what the untuned 0.5f threshold
            // (SherpaDiarisationRunner.cs:26) did on the only run on record. Labelling the whole
            // call "Local Speaker 1" is not an improvement over "Me", so commit nothing - and mark
            // it, because without a commit Diarised stays false and nothing else records the run.
            if (assignment.ClusterKeys.Count <= 1)
            {
                await MarkAsync(sessionId, Markers.SpeakerDetectionOneVoice, ct);
                await WriteDeclaredCountAsync(sessionId, mode, speakerCount, ct);
                return new SpeakerDetectionOutcome(
                    SpeakerDetectionResult.OneVoice, assignment.ClusterKeys.Count);
            }

            // --- commit ---
            var names = assignment.ClusterKeys.ToDictionary(
                key => key,
                key => DefaultSpeakerLabels.For(SourceKind.Local, ParseClusterId(key)),
                StringComparer.Ordinal);

            var commit = new DiarisationCommit(
                [SourceKind.Local],
                new Dictionary<string, IReadOnlyDictionary<string, string>>
                { [SourceKind.Local.ToString()] = assignment.SeqToClusterKey },
                names,
                result.Method,
                time.GetUtcNow());

            await maintenance.SaveDiarisationAsync(sessionId, commit, loaded.ActiveVersion,
                // No participant ownership: the import never names anyone. Passing an empty map
                // (not null) keeps the meta.json ownership pass on its normal branch.
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, DiarisationResult>(StringComparer.Ordinal)
                { [SourceKind.Local.ToString()] = result },
                ct);

            await WriteLocalCountAsync(sessionId, assignment.ClusterKeys.Count, ct);
            return new SpeakerDetectionOutcome(
                SpeakerDetectionResult.Committed, assignment.ClusterKeys.Count);
        }
        catch (OperationCanceledException)
        {
            // A choice, not a degradation. The import is already complete and valid.
            return new SpeakerDetectionOutcome(SpeakerDetectionResult.Cancelled, 0);
        }
        catch (Exception ex)
        {
            // Deliberately broad. A missing helper exe throws Win32Exception straight out of
            // ProcessDiarisationHelper.cs:33 - SherpaHelperDiariser.cs:47 does not catch it - so
            // catching only DiarisationException would let it escape and fault the whole import.
            try
            {
                await MarkAsync(sessionId, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    Markers.SpeakerDetectionFailed, ex.Message), CancellationToken.None);
                await WriteDeclaredCountAsync(sessionId, mode, speakerCount, CancellationToken.None);
            }
            catch { /* the marker is best-effort: never turn a detection fault into an import fault */ }
            return new SpeakerDetectionOutcome(SpeakerDetectionResult.Failed, 0);
        }
    }

    private static int ParseClusterId(string clusterKey)
    {
        int idx = clusterKey.IndexOf(':');
        return idx >= 0 && idx + 1 < clusterKey.Length
            && int.TryParse(clusterKey[(idx + 1)..], out int id) ? id : 0;
    }

    /// <summary>Append a marker AND correct session.json's MarkerCount. AudioImporter.cs:185-200
    /// recounts markers during the Save stage; detection runs after Save, so a bare append would
    /// leave the count stale by one.</summary>
    private Task MarkAsync(string sessionId, string text, CancellationToken ct)
        => maintenance.RunForSessionAsync(sessionId, async inner =>
        {
            var store = new TranscriptStore(paths.TranscriptJsonl(sessionId));
            await store.AppendAsync(
                TranscriptLine.Marker(await store.NextSeqAsync(inner), 0, text), inner);

            var lines = await store.ReadAllAsync(inner);
            var sessionStore = new SessionStore(paths.SessionJson(sessionId));
            if (await sessionStore.ReadAsync(inner) is { } session)
                await sessionStore.SaveAsync(
                    session with { MarkerCount = lines.Count(l => l.Kind == TranscriptKind.Marker) },
                    inner);
            return true;
        }, ct);

    /// <summary>Declared(n) is written even on the failure paths: the user asserted it, and it
    /// pre-configures the force-N button for a manual retry in Split Speakers.</summary>
    private Task WriteDeclaredCountAsync(string sessionId, SpeakerDetection mode, int? count,
        CancellationToken ct)
        => mode == SpeakerDetection.Declared && count is int n
            ? WriteLocalCountAsync(sessionId, n, ct)
            : Task.CompletedTask;

    private Task WriteLocalCountAsync(string sessionId, int count, CancellationToken ct)
        => maintenance.RunForSessionAsync(sessionId, async inner =>
        {
            var store = new MetadataStore(paths.MetaJson(sessionId));
            var meta = await store.LoadAsync(inner);
            if (meta is null || meta.LocalCount == count) return false;
            // Never flip Edited/LastEditedAtUtc - reserved for manual transcript corrections.
            await store.SaveAsync(meta with { LocalCount = count }, inner);
            return true;
        }, ct);

    private sealed class NullProgress : IProgress<double>
    {
        public static readonly NullProgress Instance = new();
        public void Report(double value) { }
    }
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SpeakerDetectionStepTests" 2>&1 | tail -5`
Expected: PASS, 13 tests.

If `Auto_commits_default_labels_and_flips_diarised` fails on the `speakers.Assignments["Local"]["0"]` assertion, check `ClusterAssigner.Assign`'s seq keying — it keys by `line.Seq.ToString()` (`ClusterAssigner.cs:44`), and the fixture seeds Local segments at seq 0 and 1.

- [ ] **Step 7: Confirm the ProbeLeg extraction broke nothing**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SplitSpeakers" 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/LocalScribe.App/Services/AudioLegProbe.cs src/LocalScribe.App/Services/SpeakerDetectionStep.cs src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs src/LocalScribe.Core/Model/Markers.cs tests/LocalScribe.App.Tests/SpeakerDetectionStepTests.cs
git commit -m "feat(import): SpeakerDetectionStep - post-import diarise and commit

Runs AFTER ImportAsync returns so a diariser failure can never reach the
Directory.Delete-the-whole-session catch at AudioImporter.cs:205-210, and
the Diarised flag is not clobbered by the Save-stage snapshot window at
:183-200. The engine call runs OUTSIDE the per-session gate - it is
minutes of CPU on a SemaphoreSlim every other writer queues on.

Catches Exception broadly, not just DiarisationException: a missing helper
exe throws Win32Exception straight out of ProcessDiarisationHelper.cs:33.

<=1 cluster commits nothing. The 0.5f auto threshold has never been
validated on real speech and collapsed to one cluster on the only run on
record; 'Local Speaker 1' across a whole call is not better than 'Me'.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YLjXhg7YDw7x6EHfGyUuYk"
```

---

### Task 6: Relax the Split Speakers source gate

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs:337-349` (the `options` block in `LoadAsync`)
- Modify: `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs` (`CanForceRun`, `:296`)
- Test: `tests/LocalScribe.App.Tests/SplitSpeakersSourceGateTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: no new API. Behaviour change: a source is offered whenever its leg exists, regardless of `meta.LocalCount`/`RemoteCount`. Task 7 relies on this — a freshly imported session must be loadable.

**Why.** `LoadAsync` currently gates each side on `meta.LocalCount > 1` (`:343`) / `meta.RemoteCount > 1` (`:347`). `SessionMeta.LocalCount` and `RemoteCount` default to `1` (`SessionMeta.cs:21,24`) and `AudioImporter` calls `SessionBootstrap.StartAsync` without ever setting them (`AudioImporter.cs:108-110`). **Split Speakers is therefore unusable on a fresh import today** — the dialog opens with zero sources and `Run`'s `CanExecute` is false, unless the user first declares 2+ participants in Session Details.

The `> 1` guard is wrong for any session whose speaker count is not known in advance. The declared count stays meaningful — it is the number the force-N button forces — so it keeps driving `CanForceCount`, and the force button is suppressed at `DeclaredCount <= 1` where forcing 1 cluster would be meaningless. The `session.EndedAtUtc is not null` guard is unrelated and stays exactly as it is: an in-progress session still offers nothing.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/SplitSpeakersSourceGateTests.cs`. Copy `MakeFinalizedSession` and `MakeVm` from `SplitSpeakersViewModelTests.cs:37-89` verbatim (per-file copies are the house convention — see that file's own comment saying the harnesses mirror each other on purpose), then add:

```csharp
    [Fact]
    public async Task A_leg_with_a_declared_count_of_one_is_still_offered()
    {
        // THE import blocker (design 2026-07-28 task 6): SessionMeta.LocalCount/RemoteCount default
        // to 1 (SessionMeta.cs:21,24) and AudioImporter never raises them
        // (AudioImporter.cs:108-110), so the old `> 1` gate made Split Speakers open EMPTY on every
        // freshly imported session - Run disabled, nothing to do.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 1, retained: [SourceKind.Remote], localCount: 1);
        var vm = MakeVm(svc, paths, engine);

        await vm.LoadAsync(id, default);

        var only = Assert.Single(vm.Sources);
        Assert.Equal(SourceKind.Remote, only.Source);
        Assert.Equal(1, only.DeclaredCount);   // the declared count is retained, just not a gate
    }

    [Fact]
    public async Task Both_legs_are_offered_when_both_are_retained()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 1, retained: [SourceKind.Local, SourceKind.Remote], localCount: 1);
        var vm = MakeVm(svc, paths, engine);

        await vm.LoadAsync(id, default);

        Assert.Equal(2, vm.Sources.Count);
    }

    [Fact]
    public async Task A_leg_with_no_audio_on_disk_is_still_not_offered()
    {
        // The relaxation is about the DECLARED COUNT only. No retained leg means nothing to read.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 3, retained: [], localCount: 3);
        var vm = MakeVm(svc, paths, engine);

        await vm.LoadAsync(id, default);

        Assert.Empty(vm.Sources);
    }

    [Fact]
    public async Task Force_N_stays_suppressed_when_the_declared_count_is_one()
    {
        // Forcing exactly 1 cluster is meaningless, and the count is a default nobody asserted.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 1, retained: [SourceKind.Remote], localCount: 1);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;
        engine.Next = new DiarisationResult(
            [new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)], 2, "fake");

        await vm.RunCommand.ExecuteAsync(null);

        Assert.True(vm.CountMismatch);                  // 2 found vs 1 declared
        Assert.False(vm.ForceCountCommand.CanExecute(null));
    }

    [Fact]
    public async Task Force_N_is_still_offered_when_a_real_count_was_declared()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 3, retained: [SourceKind.Remote], localCount: 1);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;
        engine.Next = new DiarisationResult(
            [new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)], 2, "fake");

        await vm.RunCommand.ExecuteAsync(null);

        Assert.True(vm.CountMismatch);                  // 2 found vs 3 declared
        Assert.True(vm.ForceCountCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_in_progress_session_still_offers_nothing()
    {
        // Unrelated guard, deliberately unchanged: EndedAtUtc null means not finalized.
        var paths = new StoragePaths(_root);
        string id = "live";
        Directory.CreateDirectory(paths.SessionDir(id));
        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, StartedAtUtc = DateTimeOffset.UnixEpoch, EndedAtUtc = null,
            RetainedAudioSources = [SourceKind.Remote],
        }, default);
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta { RemoteCount = 3 }, default);
        File.WriteAllBytes(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac), [1, 2, 3]);
        var svc = new MaintenanceService(paths, new FakeSettingsService(new Settings()),
            new FakeRecycleBin(), TimeProvider.System);
        var vm = MakeVm(svc, paths, new FakeEngine());

        await vm.LoadAsync(id, default);

        Assert.Empty(vm.Sources);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SplitSpeakersSourceGateTests" 2>&1 | tail -5`
Expected: FAIL — `A_leg_with_a_declared_count_of_one_is_still_offered` and `Both_legs_are_offered...` and both Force-N tests fail because no source is offered at all.

- [ ] **Step 3: Write the implementation**

In `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs`, replace the `options` block at `:337-349`:

```csharp
                var options = new List<SplitSourceOption>();
                // A source is splittable only when the session is finalized/recovered (design 4.1):
                // an in-progress session offers nothing at all.
                //
                // The declared count is NOT a gate (design 2026-07-28 task 6). It used to be
                // `> 1`, which made this dialog open EMPTY on every freshly imported session:
                // SessionMeta.LocalCount/RemoteCount default to 1 (SessionMeta.cs:21,24) and
                // AudioImporter never raises them (AudioImporter.cs:108-110). The count remains
                // meaningful as the number the force-N button forces - see CanForceRun, which
                // suppresses forcing when nobody actually declared more than one voice.
                if (session.EndedAtUtc is not null)
                {
                    string? local = ProbeLeg(sessionId, SourceKind.Local, session.RetainedAudioSources, settings.AudioFormat);
                    if (local is not null)
                        options.Add(new SplitSourceOption(SourceKind.Local, meta.LocalCount, local));

                    string? remote = ProbeLeg(sessionId, SourceKind.Remote, session.RetainedAudioSources, settings.AudioFormat);
                    if (remote is not null)
                        options.Add(new SplitSourceOption(SourceKind.Remote, meta.RemoteCount, remote));
                }
```

Then tighten `CanForceRun` at `:296`:

```csharp
    // Force-N needs a count somebody actually declared: forcing exactly 1 cluster is meaningless,
    // and 1 is the SessionMeta default nobody asserted (design 2026-07-28 task 6, now that the
    // declared count no longer gates whether a source is offered at all).
    private bool CanForceRun() => !IsRunning && CanForceCount
                                  && Sources.Any(s => s.Selected && s.DeclaredCount > 1);
```

`CanForceCount` is already recomputed at the end of `RunAsync` and already notifies via `OnCanForceCountChanged` (`:308`); `Sources.CollectionChanged` and each option's `PropertyChanged` already re-poke `RunCommand` (`:37-42`, `:381-386`). Add `ForceCountCommand.NotifyCanExecuteChanged();` alongside the existing `RunCommand.NotifyCanExecuteChanged();` in the per-option `PropertyChanged` handler in `Apply` so selecting a different source re-evaluates the new `Sources.Any(...)` term:

```csharp
            s.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SplitSourceOption.Selected))
                {
                    RunCommand.NotifyCanExecuteChanged();
                    ForceCountCommand.NotifyCanExecuteChanged();
                }
            };
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SplitSpeakersSourceGateTests" 2>&1 | tail -5`
Expected: PASS, 6 tests.

- [ ] **Step 5: Run every existing Split Speakers suite**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SplitSpeakers" 2>&1 | tail -5`
Expected: PASS. Some existing tests build sessions with `remoteCount: 1` expecting no source — if any now sees one, read it carefully: if it was asserting the old `> 1` gate, update it and add a comment naming design 2026-07-28 task 6. If it was asserting something else (system-mix suppression, retained-list behaviour), the relaxation broke something real — stop and investigate.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs tests/LocalScribe.App.Tests/SplitSpeakersSourceGateTests.cs
git commit -m "fix(speakers): offer a leg whenever its audio exists, not only when count > 1

SessionMeta.LocalCount/RemoteCount default to 1 and AudioImporter never
raises them, so LoadAsync's `meta.LocalCount > 1` gate made Split Speakers
open EMPTY on every freshly imported session - Run disabled, nothing to do.

The declared count stays meaningful as the number force-N forces, so
CanForceRun now requires a selected source with DeclaredCount > 1 rather
than acting on a default nobody asserted.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YLjXhg7YDw7x6EHfGyUuYk"
```

---

### Task 7: Hydrate Split Speakers from a committed `speakers.json`

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs` (`LoadedSession`, `LoadAsync`, `Apply`, `ConfirmAsync`)
- Test: `tests/LocalScribe.App.Tests/SplitSpeakersHydrationTests.cs` (create)

**Interfaces:**
- Consumes: `MaintenanceService.RenameSpeakersAsync` (Task 4); the relaxed source gate (Task 6).
- Produces: no new public API. `SplitSpeakersViewModel.LoadAsync` now populates `Clusters` from disk when the active version has a committed overlay, and `ConfirmAsync` routes a rename-only confirm through `RenameSpeakersAsync`.

**This is the highest-risk task in the plan.** Read all of it before writing code.

Today `Clusters` is cleared in `Apply` (`:402`) and populated **only** inside `RunAsync`'s single publish dispatch (`:497-506`). So reopening the dialog on a diarised session shows nothing until you press Run, which re-runs the entire diarisation just to type a name. The whole point of committing default labels at import time is that naming them afterwards must be free.

`ConfirmAsync` (`:655-747`) reads `_assignmentBySource` and `_resultBySource`, which only `RunAsync` sets. Hydration must reconstruct `_assignmentBySource` (trivial — `speakers.json`'s `Assignments` **is** the assignment) but **cannot** reconstruct `_resultBySource`: `DiarisationResult` carries raw segments and embedding vectors that no longer exist anywhere. That is what forces the two-mode confirm:

| Confirm mode | Condition | Write path |
|---|---|---|
| Fresh | `_resultBySource` is non-empty for every selected source | `SaveDiarisationAsync` (today's path, unchanged) |
| Rename-only | hydrated, no run in this session | `RenameSpeakersAsync` (Task 4) |

Two derived details:

- **`SnippetStartMs`** comes from `result.Segments` in `RunAsync` (`:466-474`). Hydrated rows have no segments, so derive it from the **earliest assigned transcript line's `StartMs`**. That is what the play button seeks to, and a line start is within a segment by construction.
- **Suggestions** hydrate from `embeddings.json` through the same `ComputeMatterPoolSuggestionsAsync` path, but that method takes `Dictionary<SourceKind, DiarisationResult>`. Add a sibling that reads the persisted `ClusterEmbeddings` instead. If that proves awkward, it is acceptable to ship hydration with **no** suggestion chips on hydrated rows and log nothing — suggestions are advisory-only by design (`SplitSpeakersViewModel.cs:71-73`) and the import path already wrote `embeddings.json`, so a later fresh Run still surfaces them. Prefer wiring it; do not block the task on it.

**Locked invariants you must not break:**
- `Confirm` remains the voiceprint consent gate. Nothing enrolls and no `SuggestionProvenance` is written until the user presses it. The import's automatic commit writes default labels only.
- The atomic-publish rule (`:497-519`): rows and their suggestions must reach `Clusters` in **one** dispatch turn. Hydration publishes from `Apply`, which is already inside `LoadAsync`'s single `_dispatch(() => Apply(loaded))` (`:354`) — keep it that way.
- `_versionId` is captured in `Apply` from `loaded.Session.ActiveVersion` and passed to the write path so a concurrent re-transcription cannot redirect the commit (the F1 fix). Hydration must read `speakers.json` for **that same version**.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/SplitSpeakersHydrationTests.cs`. Copy the `MakeFinalizedSession`/`MakeVm` harness from `SplitSpeakersViewModelTests.cs:37-89`, but **use the canonical `QueuedDispatch`** (copy from `SplitSpeakersViewModelVoiceprintTests.cs:29-42`) instead of `a => a()`, and give `FakeEngine` a `Calls` counter. Then:

```csharp
    /// <summary>Seeds an already-committed diarisation the way the import-time detection step
    /// leaves one: two clusters with default labels, assignments over the two Remote segments.</summary>
    private static async Task SeedCommittedDiarisationAsync(StoragePaths paths, string id)
    {
        await new SpeakersStore(paths.SpeakersJson(id)).SaveAsync(new Speakers
        {
            Names = new Dictionary<string, string>
            { ["Remote:0"] = "Remote Speaker 1", ["Remote:1"] = "Remote Speaker 2" },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["3"] = "Remote:0", ["4"] = "Remote:1" } },
            DiarisedSources = [SourceKind.Remote],
            Method = "sherpa",
            DiarisedAtUtc = DateTimeOffset.UnixEpoch,
        }, default);
    }

    [Fact]
    public async Task Load_populates_clusters_from_disk_without_calling_the_engine()
    {
        // THE regression this task exists to prevent. Before hydration, Clusters was populated only
        // inside RunAsync (:497-506), so reopening the dialog to rename a speaker re-ran the whole
        // diarisation - minutes of CPU to type a name. Asserting the ABSENCE of an engine call is
        // the point; a test that only checked Clusters.Count would pass against the old behaviour
        // the moment someone pressed Run.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.Equal(0, engine.Calls);
        Assert.Equal(2, vm.Clusters.Count);
        Assert.Equal("Remote Speaker 1", vm.Clusters[0].Name);
        Assert.Equal("Remote:0", vm.Clusters[0].ClusterKey);
        Assert.Equal(SourceKind.Remote, vm.Clusters[0].Source);
    }

    [Fact]
    public async Task Hydrated_rows_carry_previews_and_a_snippet_offset()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        // MakeFinalizedSession seeds Remote seq 3 = "hello" @0ms and seq 4 = "world" @1000ms.
        Assert.Contains("hello", vm.Clusters[0].PreviewLines);
        Assert.Equal(0, vm.Clusters[0].SnippetStartMs);
        Assert.Equal(1000, vm.Clusters[1].SnippetStartMs);
    }

    [Fact]
    public async Task A_hydrated_rename_persists_without_running_the_engine()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;
        vm.Clusters[0].Name = "Sarah Chen";

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Equal(0, engine.Calls);
        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Sarah Chen", s!.Names["Remote:0"]);
        Assert.Equal("Remote Speaker 2", s.Names["Remote:1"]);   // untouched row keeps its label
    }

    [Fact]
    public async Task A_hydrated_rename_does_not_restamp_the_diarisation()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;
        vm.Clusters[0].Name = "Sarah Chen";

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal(DateTimeOffset.UnixEpoch, s!.DiarisedAtUtc);
        Assert.Equal("sherpa", s.Method);
        Assert.Equal("Remote:0", s.Assignments["Remote"]["3"]);
    }

    [Fact]
    public async Task A_fresh_run_after_hydration_still_uses_the_full_commit_path()
    {
        // Hydration must not turn a real re-diarise into a rename: a fresh run has segments and
        // embeddings, and its commit has to go through SaveDiarisationAsync/SpeakersMerge.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;
        engine.Next = new DiarisationResult(
            [new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)], 2, "fresh-run");

        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();
        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Equal(1, engine.Calls);
        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("fresh-run", s!.Method);   // restamped, unlike a rename
    }

    [Fact]
    public async Task An_undiarised_session_hydrates_nothing()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.Empty(vm.Clusters);
        Assert.Single(vm.Sources);              // still offered, so Run is available
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task Hydrated_rows_are_never_visible_without_their_state()
    {
        // Atomic-publish invariant (:497-519): pump one turn at a time and assert no turn ever
        // exposes a half-built Clusters collection. Hydration publishes inside LoadAsync's single
        // _dispatch(() => Apply(loaded)) turn - keep it that way.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        while (dispatcher.PumpOne())
            Assert.True(vm.Clusters.Count is 0 or 2);
    }

    [Fact]
    public async Task Confirm_is_still_refused_when_a_selected_source_was_never_run_or_hydrated()
    {
        // The Task 8 precondition at :385-389 must survive: a selected source with no assignment
        // must not persist an incomplete "diarised" commit.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 2, retained: [SourceKind.Local, SourceKind.Remote], localCount: 2);
        await SeedCommittedDiarisationAsync(paths, id);   // Remote only
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        foreach (var s in vm.Sources) s.Selected = true;   // Local has no assignment

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.False(speakers!.Names.ContainsKey("Local:0"));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SplitSpeakersHydrationTests" 2>&1 | tail -5`
Expected: FAIL — `Clusters` is empty after `LoadAsync` on a diarised session.

- [ ] **Step 3: Read the committed overlay in `LoadAsync`**

In `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs`, widen the private `LoadedSession` record (`:312-313`):

```csharp
    private sealed record LoadedSession(SessionRecord Session, SessionMeta Meta,
        IReadOnlyList<TranscriptLine> Lines, List<SplitSourceOption> Sources,
        Speakers? Committed);
```

Inside `LoadAsync`'s gated work (after the `options` block, before the return), read the overlay for the version this dialog is loading:

```csharp
                // Hydration (design 2026-07-28 task 7): the committed overlay for the version this
                // dialog is about to pin, so reopening to rename a speaker never re-runs the
                // diariser. Read under the same gate hold as everything else here.
                var committed = await new SpeakersStore(
                    paths.SpeakersJson(sessionId, session.ActiveVersion)).LoadAsync(token);

                return new LoadedSession(session, meta, lines, options, committed);
```

> Note `_paths` inside the lambda is the VM field; the existing code already uses `_paths.TranscriptJsonl(...)` there, so write `_paths.SpeakersJson(...)`.

- [ ] **Step 4: Build the hydrated rows in `Apply`**

Replace the tail of `Apply` (`:402-408`) so the same single dispatch turn that publishes `Sources` also publishes `Clusters`:

```csharp
        Clusters.Clear();
        CountMismatch = false;
        CanForceCount = false;
        ForceCountLabel = "";
        Progress = 0;
        _resultBySource = new Dictionary<SourceKind, DiarisationResult>();
        _assignmentBySource = new Dictionary<SourceKind, ClusterAssignment>();

        // Hydration (design 2026-07-28 task 7). Rebuild the naming rows from the committed overlay
        // with NO engine call. _resultBySource stays empty on purpose: a hydrated row has no
        // DiarisedSegment list and no embedding vectors, and that emptiness is exactly what
        // ConfirmAsync uses to choose the rename-only write path (a commit through
        // SaveDiarisationAsync/SpeakersMerge would treat these existing keys as FRESH keys and
        // remap any that collide with a pin - duplicating one voice across two rows).
        if (loaded.Committed is { } committed)
            HydrateClusters(committed);
    }

    private void HydrateClusters(Speakers committed)
    {
        foreach (var source in new[] { SourceKind.Local, SourceKind.Remote })
        {
            if (!committed.Assignments.TryGetValue(source.ToString(), out var seqToKey)) continue;
            if (seqToKey.Count == 0) continue;

            var assignment = new ClusterAssignment(
                new Dictionary<string, string>(seqToKey, StringComparer.Ordinal),
                seqToKey.Values.Distinct(StringComparer.Ordinal)
                    .OrderBy(k => ParseClusterId(k)).ToList());
            _assignmentBySource[source] = assignment;

            var candidates = source == SourceKind.Local ? _localCandidates : _remoteCandidates;
            var wanted = source == SourceKind.Local ? TranscriptSource.Local : TranscriptSource.Remote;

            foreach (string clusterKey in assignment.ClusterKeys)
            {
                int clusterId = ParseClusterId(clusterKey);
                string defaultName = DefaultSpeakerLabels.For(source, clusterId);
                // A hydrated row has no DiarisedSegment list, so the snippet offset comes from the
                // earliest transcript line assigned to this cluster - within a segment by
                // construction, and exactly what the play button needs to seek to.
                long? snippetStartMs = _lines
                    .Where(l => l.Kind == TranscriptKind.Segment && l.Source == wanted
                                && assignment.SeqToClusterKey.TryGetValue(l.Seq.ToString(), out string? k)
                                && k == clusterKey)
                    .Select(l => (long?)l.StartMs)
                    .DefaultIfEmpty(null)
                    .Min();

                var row = new ClusterRowViewModel(clusterKey, source, clusterId, defaultName,
                    PreviewLinesFor(source, assignment, clusterKey), snippetStartMs, candidates);
                // The committed name wins over the default label; a blank/absent entry keeps it.
                if (committed.Names.TryGetValue(clusterKey, out string? name)
                    && !string.IsNullOrWhiteSpace(name))
                    row.Name = name;
                Clusters.Add(row);
            }
        }
    }
```

> `_lines` and `_localCandidates`/`_remoteCandidates` are assigned earlier in `Apply`, so `HydrateClusters` must be called after them — the placement above satisfies that. `ParseClusterId` and `PreviewLinesFor` already exist (`:619-639`).

- [ ] **Step 5: Route a rename-only confirm through `RenameSpeakersAsync`**

In `ConfirmAsync`, after the `names` / `owned` / `provenance` / `enrollmentIntents` blocks are built and **before** the `DiarisationCommit` is constructed (`:737`), branch:

```csharp
            // Rename-only confirm (design 2026-07-28 task 7): every selected source was hydrated
            // from disk, not run in this dialog, so there are no segments or embeddings to commit.
            // Routing this through SaveDiarisationAsync would be wrong, not merely wasteful:
            // SpeakersMerge treats the commit's keys as FRESH and remaps any that collide with a
            // pinned or owned key - and on a rename the "fresh" keys ARE the existing keys, so a
            // pinned cluster would collide with itself and be duplicated under a new id.
            bool renameOnly = selected.All(s => !_resultBySource.ContainsKey(s.Source));
            if (renameOnly)
            {
                bool wrote = await _maintenance.RenameSpeakersAsync(
                    _sessionId, _versionId, names, owned, provenance, CancellationToken.None);
                if (wrote)
                {
                    // No remap: the keys already landed in speakers.json.
                    await EnrollConfirmedVoicesAsync(
                        enrollmentIntents, new Dictionary<string, string>(StringComparer.Ordinal));
                    _dispatch(() => DiarisationSaved?.Invoke(_sessionId));
                }
                return;
            }

            var commit = new DiarisationCommit(sources, assignments, names, method, _time.GetUtcNow(), provenance);
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SplitSpeakersHydrationTests" 2>&1 | tail -5`
Expected: PASS, 8 tests.

If `A_fresh_run_after_hydration_still_uses_the_full_commit_path` fails with `Method` still `"sherpa"`, the `renameOnly` predicate is wrong — `RunAsync` replaces `_resultBySource` wholesale in its publish dispatch (`:499`), so make sure the test pumps the dispatcher after `RunCommand` before confirming.

- [ ] **Step 7: Run every Split Speakers suite**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SplitSpeakers" 2>&1 | tail -5`
Expected: PASS — including `SplitSpeakersViewModelTests`, `SplitSpeakersClusterKeyTests`, `SplitSpeakersPickerTests` and `SplitSpeakersViewModelVoiceprintTests`.

- [ ] **Step 8: Wire suggestions onto hydrated rows (optional within this task)**

If `ComputeMatterPoolSuggestionsAsync` can be given a sibling that reads the persisted `ClusterEmbeddings` for `_versionId` instead of a `Dictionary<SourceKind, DiarisationResult>`, call it from `LoadAsync` (off the dispatch, before `_dispatch(() => Apply(loaded))`) and stamp each row's `Suggestion` inside `HydrateClusters`, exactly as `RunAsync` does inside its single publish turn. Add:

```csharp
    [Fact]
    public async Task Hydrated_rows_carry_voiceprint_suggestions_from_the_persisted_embeddings()
```

modelled on `SplitSpeakersViewModelVoiceprintTests.Run_populates_matter_pool_suggestion_on_row` but seeding `embeddings.json` directly instead of returning them from the fake engine.

If it turns out to require reshaping the matcher's inputs more than trivially, **skip it and say so in the commit message**: suggestions are advisory-only (`SplitSpeakersViewModel.cs:71-73`), the import already wrote `embeddings.json`, and a later fresh Run still surfaces chips. Do not block the task.

- [ ] **Step 9: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs tests/LocalScribe.App.Tests/SplitSpeakersHydrationTests.cs
git commit -m "feat(speakers): hydrate Split Speakers from a committed speakers.json

Clusters were populated only inside RunAsync, so reopening the dialog to
rename a speaker re-ran the entire diarisation. LoadAsync now rebuilds the
naming rows from disk with no engine call, and a confirm on hydrated rows
routes through RenameSpeakersAsync.

_resultBySource stays empty on a hydrated load on purpose - that emptiness
is what selects the rename-only write path. A hydrated commit through
SpeakersMerge would treat the existing keys as FRESH and remap any that
collide with a pin, duplicating one voice across two rows.

SnippetStartMs derives from the earliest assigned transcript line, since a
hydrated row has no DiarisedSegment list.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YLjXhg7YDw7x6EHfGyUuYk"
```

---

### Task 8: Import dialog — the Speakers control, the availability gate, the detect stage

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs` (delegate `:12-16`; ctor `:42-60`; properties `:62-113`; `StartAsync` `:179-220`; `OnStageForProgress` `:230-241`; `DispatchProgress` `:273-296`)
- Modify: `src/LocalScribe.App/ImportDialog.xaml`
- Test: `tests/LocalScribe.App.Tests/ImportDialogSpeakerDetectionTests.cs` (create)

**Interfaces:**
- Consumes: `SpeakerDetection`, `ImportStage.DetectSpeakers` (Task 1).
- Produces:
  ```csharp
  public delegate Task<string> ImportRunner(ImportRequest request, IProgress<ImportStage> progress,
      IProgress<TranscriptionProgress> transcriptProgress, IProgress<double> diariseProgress,
      Func<DurationMismatchInfo, Task<bool>> confirmDurationMismatch, CancellationToken ct);

  public sealed record SpeakerChoice(string Label, SpeakerDetection Mode, int? Count)
  { public override string ToString() => Label; }
  ```
  plus VM members `SpeakerChoices`, `SelectedSpeakerChoice`, `SpeakerDetectionUnavailableReason`, `CanChooseSpeakers`, `IsDetectingSpeakers`, `DetectProgress`, `DetectProgressText`. Task 9 supplies the new ctor argument and the new delegate parameter.

**Context you need.** The VM ctor has exactly 9 parameters in this order: `(IAudioDecoder decoder, ImportRunner runImport, MaintenanceService maintenance, Func<IReadOnlySet<string>> availableModels, Func<OpenPathRequest,string?> pickOpenPath, Func<DurationMismatchInfo,Task<bool>> confirmMismatch, IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time)`. `availableModels` is the only one with no backing field — it is consumed once in the ctor body, so the model list is a snapshot. Follow that precedent: the availability reason is also probed **once at construction**, so a helper that vanishes mid-dialog is caught by `SpeakerDetectionStep`'s own re-check (Task 5) rather than by live UI polling.

Two mandatory changes, or the UI actively lies:
- `DispatchProgress`'s stage switch (`:280-286`) has **no** `ImportStage.Save` arm — Save is handled by the `_ =>` catch-all that prints `"Saving session..."`. The compiler will **not** flag the new `DetectSpeakers` member; add an explicit arm by hand.
- `IsTranscribing` is set on `Transcribe` and cleared **only** on `Save` (`:230-241`). Since `DetectSpeakers` is reported after `Save`, the transcription bar is already cleared — but `IsDetectingSpeakers` must be set there and cleared in `StartAsync`'s `finally` alongside `IsTranscribing`.

The transcription `ProgressBar` uses `Minimum="0" Maximum="1"` with a 0..1 double — the detect bar must match. The progress text separator in the existing ETA is a literal U+00B7 MIDDLE DOT; the detect text needs no ETA (**no measured RTF baseline exists for the diariser anywhere in the repo**, so any estimate would be invented).

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/ImportDialogSpeakerDetectionTests.cs`. Copy the harness shape from the existing `ImportDialogViewModelTests.cs` (read it first — it already has a `FakeDecoder`, an `ImportRunner` fake and a reporter), then add:

```csharp
    [Fact]
    public void Defaults_to_detect_automatically()
    {
        var vm = MakeVm();
        Assert.Equal(SpeakerDetection.Auto, vm.SelectedSpeakerChoice!.Mode);
        Assert.Null(vm.SelectedSpeakerChoice.Count);
    }

    [Fact]
    public void Offers_off_auto_and_counts_two_through_six()
    {
        var vm = MakeVm();
        Assert.Equal(SpeakerDetection.Off, vm.SpeakerChoices[0].Mode);
        Assert.Equal(SpeakerDetection.Auto, vm.SpeakerChoices[1].Mode);
        var counts = vm.SpeakerChoices.Where(c => c.Mode == SpeakerDetection.Declared)
            .Select(c => c.Count).ToList();
        Assert.Equal([2, 3, 4, 5, 6], counts);
        // The dropdown never offers a count below 2 - ImportRequest would throw, and
        // SherpaDiarisationRunner.cs:23 would silently take the auto path for a 0.
        Assert.DoesNotContain(vm.SpeakerChoices, c => c.Count is int n && n < 2);
    }

    [Fact]
    public void The_control_is_suppressed_for_a_declared_channel_split()
    {
        var vm = MakeVm();
        vm.IsStereo = true;
        vm.EachPartyOwnChannel = true;
        // Split-stereo already has speakers by channel; detection is not offered.
        Assert.False(vm.CanChooseSpeakers);
    }

    [Fact]
    public void A_stereo_file_the_user_did_not_split_still_offers_detection()
    {
        // Downmix is the DEFAULT answer, and is exactly the case that needs detection.
        var vm = MakeVm();
        vm.IsStereo = true;
        vm.EachPartyOwnChannel = false;
        Assert.True(vm.CanChooseSpeakers);
    }

    [Fact]
    public void An_unavailable_helper_disables_the_control_with_a_visible_reason()
    {
        var vm = MakeVm(unavailable: () => "Speaker detection unavailable - LocalScribe.Diarizer.exe is not installed.");
        Assert.False(vm.CanChooseSpeakers);
        Assert.Contains("LocalScribe.Diarizer.exe", vm.SpeakerDetectionUnavailableReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_writes_the_chosen_mode_and_count_onto_the_request()
    {
        ImportRequest? captured = null;
        var vm = MakeVm(run: (req, _, _, _, _, _) => { captured = req; return Task.FromResult("s1"); });
        await PickAndFillAsync(vm);
        vm.SelectedSpeakerChoice = vm.SpeakerChoices.First(c => c.Count == 3);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(SpeakerDetection.Declared, captured!.SpeakerDetection);
        Assert.Equal(3, captured.SpeakerCount);
    }

    [Fact]
    public async Task Start_sends_Off_when_the_control_is_suppressed_by_a_channel_split()
    {
        ImportRequest? captured = null;
        var vm = MakeVm(run: (req, _, _, _, _, _) => { captured = req; return Task.FromResult("s1"); });
        await PickAndFillAsync(vm);
        vm.IsStereo = true;
        vm.EachPartyOwnChannel = true;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(SpeakerDetection.Off, captured!.SpeakerDetection);
        Assert.Null(captured.SpeakerCount);
    }

    [Fact]
    public async Task Start_sends_Off_when_the_helper_is_unavailable()
    {
        ImportRequest? captured = null;
        var vm = MakeVm(unavailable: () => "no helper",
            run: (req, _, _, _, _, _) => { captured = req; return Task.FromResult("s1"); });
        await PickAndFillAsync(vm);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(SpeakerDetection.Off, captured!.SpeakerDetection);
    }

    [Fact]
    public async Task The_detect_stage_gets_its_own_text_not_the_saving_catch_all()
    {
        // ImportDialogViewModel.cs:280-286 has a `_ =>` catch-all printing "Saving session..." and
        // NO explicit Save arm, so a new ImportStage member renders as "Saving session..." with no
        // compiler warning. This test is the only thing that catches that.
        var dispatcher = new QueuedDispatch();
        IProgress<ImportStage>? stages = null;
        var vm = MakeVm(dispatch: dispatcher.Dispatch,
            run: (_, p, _, _, _, _) => { stages = p; return Task.FromResult("s1"); });
        await PickAndFillAsync(vm);
        await vm.StartCommand.ExecuteAsync(null);

        stages!.Report(ImportStage.DetectSpeakers);
        dispatcher.Pump();

        Assert.Contains("speaker", vm.StageText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Saving", vm.StageText, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.IsDetectingSpeakers);
        Assert.False(vm.IsTranscribing);
    }

    [Fact]
    public async Task Detect_progress_drives_a_determinate_bar_and_flips_to_matching_at_the_end()
    {
        // The helper's embedding-extraction tail emits NO progress (Diarizer/Program.cs:61-72), so a
        // bar parked at 100% reads as a hang. At 1.0 the text says what is still happening.
        var dispatcher = new QueuedDispatch();
        IProgress<double>? detect = null;
        var vm = MakeVm(dispatch: dispatcher.Dispatch,
            run: (_, _, _, d, _, _) => { detect = d; return Task.FromResult("s1"); });
        await PickAndFillAsync(vm);
        await vm.StartCommand.ExecuteAsync(null);

        detect!.Report(0.4);
        dispatcher.Pump();
        Assert.Equal(0.4, vm.DetectProgress, 3);
        Assert.Contains("40", vm.DetectProgressText, StringComparison.Ordinal);

        detect.Report(1.0);
        dispatcher.Pump();
        Assert.Contains("Matching voices", vm.DetectProgressText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detect_state_is_cleared_when_the_import_settles()
    {
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(dispatch: dispatcher.Dispatch);
        await PickAndFillAsync(vm);

        await vm.StartCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.False(vm.IsDetectingSpeakers);
        Assert.False(vm.IsBusy);
    }
```

Write `MakeVm(...)` with optional `unavailable`, `run` and `dispatch` parameters defaulting to a null-returning probe, a trivial runner and `a => a()`, and a `PickAndFillAsync(vm)` helper that sets `SourcePath`/`Title`/`RecordedAtText` so `CanStart()` passes — mirror how the existing `ImportDialogViewModelTests` does it. Copy the canonical `QueuedDispatch` (with `PumpOne`) from `SplitSpeakersViewModelVoiceprintTests.cs:29-42`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ImportDialogSpeakerDetectionTests" 2>&1 | tail -5`
Expected: FAIL — none of the new members exist and `ImportRunner` has 5 parameters.

- [ ] **Step 3: Widen the delegate and add the choice type**

In `src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs`, replace lines 12-16:

```csharp
/// <summary>The import seam the window layer binds to: AudioImporter.ImportAsync followed by the
/// post-import speaker-detection phase (design 2026-07-28 section 3). Tests pass a fake so the VM
/// is exercised with no FFmpeg, engine or diariser on disk.</summary>
public delegate Task<string> ImportRunner(ImportRequest request, IProgress<ImportStage> progress,
    IProgress<TranscriptionProgress> transcriptProgress,
    IProgress<double> diariseProgress,
    Func<DurationMismatchInfo, Task<bool>> confirmDurationMismatch, CancellationToken ct);

/// <summary>One entry in the Import dialog's Speakers dropdown. ToString() returns the label so a
/// plain ComboBox renders it without a DisplayMemberPath (same idiom as SpeakerCandidate).</summary>
public sealed record SpeakerChoice(string Label, SpeakerDetection Mode, int? Count)
{
    public override string ToString() => Label;
}
```

Add `using LocalScribe.Core.Import;` — already present at line 22.

- [ ] **Step 4: Add the VM members**

Add a field beside the others (`:30-40`):

```csharp
    private readonly string? _speakerDetectionUnavailable;
```

Add the ctor parameter as a **new trailing optional** so nothing else has to change if a caller lags:

```csharp
    public ImportDialogViewModel(IAudioDecoder decoder, ImportRunner runImport,
        MaintenanceService maintenance, Func<IReadOnlySet<string>> availableModels,
        Func<OpenPathRequest, string?> pickOpenPath,
        Func<DurationMismatchInfo, Task<bool>> confirmMismatch,
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time,
        Func<string?>? speakerDetectionUnavailable = null)
```

and in the body, after the `SelectedModel` line:

```csharp
        // Probed ONCE at construction, matching how availableModels snapshots the model list: a
        // helper that vanishes mid-dialog is caught by SpeakerDetectionStep's own re-check.
        _speakerDetectionUnavailable = speakerDetectionUnavailable?.Invoke();
        SpeakerChoices =
        [
            new SpeakerChoice("Don't detect speakers", SpeakerDetection.Off, null),
            new SpeakerChoice("Detect automatically", SpeakerDetection.Auto, null),
            .. Enumerable.Range(2, 5).Select(n =>
                new SpeakerChoice(n.ToString(CultureInfo.InvariantCulture), SpeakerDetection.Declared, n)),
        ];
        SelectedSpeakerChoice = SpeakerChoices[1];   // Detect automatically
```

Add the properties beside the stereo trio (`:112-115`):

```csharp
    // --- speaker detection (design 2026-07-28) ---
    public IReadOnlyList<SpeakerChoice> SpeakerChoices { get; }
    [ObservableProperty] private SpeakerChoice? _selectedSpeakerChoice;

    /// <summary>Null when the helper exe and both sherpa models are present; else the visible
    /// reason the Speakers control is disabled. Import still runs, just undiarised.</summary>
    public string? SpeakerDetectionUnavailableReason => _speakerDetectionUnavailable;

    /// <summary>False when the helper is unavailable, or when the user declared a channel split -
    /// those legs already have speakers by channel. Deliberately TRUE for a 2-channel file the
    /// user did NOT split: downmix is the default answer and the case that needs detection.</summary>
    public bool CanChooseSpeakers =>
        _speakerDetectionUnavailable is null && !(IsStereo && EachPartyOwnChannel);

    // --- speaker-detection progress ---
    [ObservableProperty] private bool _isDetectingSpeakers;
    [ObservableProperty] private double _detectProgress;          // 0..1 for the determinate bar
    [ObservableProperty] private string _detectProgressText = "";
```

Notify `CanChooseSpeakers` when its inputs change — add beside the existing partial hooks (`:118-122`):

```csharp
    partial void OnIsStereoChanged(bool value) => OnPropertyChanged(nameof(CanChooseSpeakers));
    partial void OnEachPartyOwnChannelChanged(bool value) => OnPropertyChanged(nameof(CanChooseSpeakers));
```

- [ ] **Step 5: Thread the choice through `StartAsync`**

In the `ImportRequest` initializer (`:190-200`), after `Language`:

```csharp
                SpeakerDetection = EffectiveSpeakerDetection(),
                SpeakerCount = EffectiveSpeakerDetection() == SpeakerDetection.Declared
                    ? SelectedSpeakerChoice!.Count : null,
```

and add the helper next to `CanStart`:

```csharp
    /// <summary>The mode actually sent to the importer: Off whenever the control is suppressed
    /// (declared channel split) or disabled (helper unavailable), so a stale selection can never
    /// queue a detection pass the UI said would not happen.</summary>
    private SpeakerDetection EffectiveSpeakerDetection() =>
        CanChooseSpeakers && SelectedSpeakerChoice is { } choice ? choice.Mode : SpeakerDetection.Off;
```

Update the `_runImport` call to pass the new channel:

```csharp
            string id = await _runImport(request, new DispatchProgress(this),
                new TranscriptDispatchProgress(this), new DetectDispatchProgress(this),
                _confirmMismatch, _cts.Token);
```

and clear the detect state in the `finally` alongside `IsTranscribing` (`:217`):

```csharp
            IsTranscribing = false;
            IsDetectingSpeakers = false;
            DetectProgress = 0;
            DetectProgressText = "";
```

Also reset them at the top of `StartAsync` beside the existing resets (`:184-187`):

```csharp
        IsDetectingSpeakers = false;
        DetectProgress = 0;
        DetectProgressText = "";
```

- [ ] **Step 6: Handle the new stage and its progress**

In `OnStageForProgress` (`:230-241`), add a `DetectSpeakers` arm:

```csharp
        if (stage == ImportStage.DetectSpeakers)
        {
            IsTranscribing = false;         // belt and braces: Save already cleared it
            IsDetectingSpeakers = true;
            DetectProgress = 0;
            DetectProgressText = "0%";
        }
```

In `DispatchProgress`'s stage switch (`:280-286`), add an explicit arm **before** the `_ =>` catch-all:

```csharp
                ImportStage.DetectSpeakers => "Detecting speakers...",
```

Add the third progress channel beside `DispatchProgress`/`TranscriptDispatchProgress` (`:273-296`), following the same explicit-dispatch pattern (never `Progress<T>` — it captures a `SynchronizationContext` unit tests do not have):

```csharp
    private sealed class DetectDispatchProgress(ImportDialogViewModel owner) : IProgress<double>
    {
        public void Report(double value) => owner._dispatch(() => owner.OnDetectProgress(value));
    }

    private void OnDetectProgress(double fraction)
    {
        DetectProgress = Math.Clamp(fraction, 0, 1);
        // No ETA: there is no measured RTF baseline for the diariser anywhere in this repo, so any
        // estimate would be invented. At 1.0 the helper is still extracting embeddings and reports
        // no further progress (Diarizer/Program.cs:61-72) - say so rather than park a full bar.
        DetectProgressText = DetectProgress >= 1.0
            ? "Matching voices..."
            : DetectProgress.ToString("P0", CultureInfo.CurrentCulture);
    }
```

- [ ] **Step 7: Add the XAML**

In `src/LocalScribe.App/ImportDialog.xaml`, after the stereo block (around line 62) and before the Matters section, add:

```xml
        <!-- Speaker detection (design 2026-07-28). Deliberately OUTSIDE the IsStereo-gated panel
             above: mono and downmixed-stereo are exactly the imports that need it. -->
        <StackPanel Margin="0,8,0,0">
            <TextBlock Text="Speakers" />
            <ComboBox ItemsSource="{Binding SpeakerChoices}"
                      SelectedItem="{Binding SelectedSpeakerChoice}"
                      IsEnabled="{Binding CanChooseSpeakers}"
                      Margin="0,2,0,0" />
            <TextBlock Style="{StaticResource MutedText}" TextWrapping="Wrap" Margin="0,2,0,0"
                       Text="{Binding SpeakerDetectionUnavailableReason}"
                       Visibility="{Binding SpeakerDetectionUnavailableReason,
                                    Converter={StaticResource NullToCollapsedConverter}}" />
        </StackPanel>

        <!-- Detect-speakers progress: determinate 0..1, same shape as the transcription bar. -->
        <StackPanel Margin="0,8,0,0"
                    Visibility="{Binding IsDetectingSpeakers,
                                 Converter={StaticResource BoolToVisibilityConverter}}">
            <ProgressBar Minimum="0" Maximum="1" Height="6"
                         Value="{Binding DetectProgress, Mode=OneWay}" />
            <TextBlock Style="{StaticResource MutedText}" Margin="0,2,0,0"
                       Text="{Binding DetectProgressText}" />
        </StackPanel>
```

> **Read the existing XAML first.** It already shows/hides the transcription block off `IsTranscribing`, so copy whatever converter or `Style.Triggers` idiom is actually in the file rather than assuming `BoolToVisibilityConverter`/`NullToCollapsedConverter` exist. If there is no null converter, bind the reason's `Visibility` off a new `bool` VM property (`HasSpeakerDetectionReason => _speakerDetectionUnavailable is not null`) using the same bool idiom the file already uses. The dialog is a plain `<Window>` with `SizeToContent="Height"`, fixed `Width=480`, `ResizeMode=NoResize` — two new rows grow it vertically with no scroll, which is fine.
>
> The `MutedText` style is a `StaticResource` the dialog does **not** define; it comes from app resources. Keep using it for consistency.

- [ ] **Step 8: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ImportDialogSpeakerDetectionTests" 2>&1 | tail -5`
Expected: PASS, 11 tests.

- [ ] **Step 9: Fix the other `ImportRunner` call sites**

The delegate gained a parameter, so every fake breaks.

Run: `grep -rn "ImportRunner\|_runImport" tests/ src/ --include=*.cs`

Update each fake lambda to the 6-parameter shape. Then:

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ImportDialog" 2>&1 | tail -5`
Expected: PASS — the pre-existing `ImportDialogViewModelTests` included.

`src/LocalScribe.App/App.xaml.cs:594` will not compile until Task 9. That is expected; do **not** patch it here beyond what Task 9 specifies. If you need a green build to commit, apply Task 9 first and commit both together.

- [ ] **Step 10: Commit**

```bash
git add src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs src/LocalScribe.App/ImportDialog.xaml tests/LocalScribe.App.Tests/
git commit -m "feat(import): Speakers control, availability gate and detect-stage progress

The dropdown offers Don't detect / Detect automatically (default) / 2-6.
It never offers a count below 2 - ImportRequest would throw, and
SherpaDiarisationRunner.cs:23 would silently take the auto path for a 0.

Suppressed for a declared channel split, disabled with a visible reason
when the helper or a sherpa model is missing; either way StartAsync sends
Off so a stale selection cannot queue a pass the UI said would not happen.

DispatchProgress's stage switch gets an EXPLICIT DetectSpeakers arm: it has
no Save arm and a '_ =>' catch-all, so a new enum member renders as
'Saving session...' with no compiler warning.

No ETA on the detect bar - there is no measured RTF baseline for the
diariser in this repo. At 1.0 the text says 'Matching voices...' because
the helper's embedding tail reports no further progress.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YLjXhg7YDw7x6EHfGyUuYk"
```

---

### Task 9: Two-phase `importRunner` + completion routing in `App.xaml.cs`

**Files:**
- Modify: `src/LocalScribe.App/App.xaml.cs:579-628` (the whole import region)
- Test: none directly — `App.xaml.cs` is composition wiring with no test seam. The behaviour it composes is covered by Tasks 5, 7 and 8. Verification is a build plus the existing suites.

**Interfaces:**
- Consumes: `SpeakerDetectionStep` + `SpeakerDetectionResult` (Task 5), the widened `ImportRunner` and the new ctor parameter (Task 8), `DiarisationAvailability` (Task 3).
- Produces: nothing new.

**Context you need.** The current region:

```csharp
579        string? importBusy = null;
580        var priorEngineBusy = comp.Controller.ExternalEngineBusy;
581        comp.Controller.ExternalEngineBusy = () => importBusy ?? priorEngineBusy?.Invoke();
582        Action openImport = () =>
...
594            ViewModels.ImportRunner runImport = async (req, progress, transcriptProgress, confirm, ct) =>
...
602                if (comp.Controller.State != LocalScribe.Core.Live.SessionState.Idle) throw ...
605                if (comp.Controller.ExternalEngineBusy?.Invoke() is string engineBusy) throw ...
608                importBusy = "audio import";
614                try { return await Task.Run(() => importer.ImportAsync(req, progress, confirm, ct, transcriptProgress), ct); }
615                finally { importBusy = null; }
...
620            importVm.Completed += id => { UpsertRowAsync; ReindexSessionAsync; _semanticIndex?.Enqueue; openReadView(id); };
```

Three things to preserve exactly:
- **The capture-then-chain idiom at 580-581.** `ExternalEngineBusy` is a settable property, not an event. A bare assignment after 581 silently drops both the import and re-transcription lanes.
- **The probe-before-set order at 602-608.** The re-check runs *before* `importBusy` is set so the lane cannot see itself (the comment at 600-601 says so explicitly).
- **`Task.Run`.** `ImportAsync` is CPU-heavy and the dialog VM awaits it on the UI thread; without this the model load and full-file transcribe freeze the dialog and starve Cancel.

Two things to fix while here:
- **`importBusy` is a non-volatile captured local** written on a `Task.Run` thread and read from `StartAsync` on another (adjacent fix 4). Its lifetime is about to grow to cover a second phase, so give it a proper home.
- **`importBusy` must stay set across detection**, or a live recording can start mid-diarise. Move the `finally` to wrap both phases.

- [ ] **Step 1: Add the lane-state holder**

At the top of the same method, replacing line 579:

```csharp
        // Cross-thread state for the import lane. `importBusy` used to be a plain captured local
        // written on the Task.Run thread and read from SessionController.StartAsync on another
        // (design 2026-07-28 adjacent fix 4); its lifetime now spans two phases, so it gets a
        // volatile home. DetectionOutcome is written by the runner and read by the Completed
        // handler on the UI thread, and routes the dialog's final destination.
        var importLane = new ImportLaneState();
        var priorEngineBusy = comp.Controller.ExternalEngineBusy;   // MARKED CALL SITE (seam name)
        comp.Controller.ExternalEngineBusy = () => importLane.BusyReason ?? priorEngineBusy?.Invoke();
```

Add the holder as a private nested type on the `App` class (near the other private helpers, e.g. beside `SyncLeaseAsAsync`):

```csharp
    /// <summary>Cross-thread slots for the audio-import lane. Both phases of an import run on a
    /// Task.Run thread while SessionController.StartAsync reads the busy reason on the UI thread,
    /// and the ImportDialogViewModel.Completed handler reads the outcome on the UI thread after the
    /// runner returns. `volatile` on reference-typed fields is legal and is what makes those
    /// hand-offs defined rather than merely observed-to-work.</summary>
    private sealed class ImportLaneState
    {
        private volatile string? _busyReason;
        private volatile object? _detectionOutcome;

        /// <summary>Non-null while an import (including its detection phase) owns an engine.</summary>
        public string? BusyReason { get => _busyReason; set => _busyReason = value; }

        /// <summary>The last run's SpeakerDetectionResult, or null when detection did not run.
        /// Boxed through `object?` because `volatile` is not permitted on a nullable enum field;
        /// the cast back is safe because this is the only writer.</summary>
        public Services.SpeakerDetectionResult? DetectionOutcome
        {
            get => (Services.SpeakerDetectionResult?)_detectionOutcome;
            set => _detectionOutcome = value;
        }
    }
```

- [ ] **Step 2: Make the runner two-phase**

Inside `openImport`, after the `importer` construction (line 592), build the detection step, then replace the runner:

```csharp
            string diarizerExe = System.IO.Path.Combine(
                AppContext.BaseDirectory, "LocalScribe.Diarizer.exe");
            var detection = new Services.SpeakerDetectionStep(comp.Diarisation, comp.Maintenance,
                comp.Paths, comp.Settings, LocalScribe.Core.Transcription.ModelPaths.Resolve,
                diarizerExe, TimeProvider.System);

            ViewModels.ImportRunner runImport =
                async (req, progress, transcriptProgress, diariseProgress, confirm, ct) =>
            {
                // B3-5 (whole-branch M-1): re-check the one-engine rule at import START, not just
                // when this dialog opened. Runs BEFORE the busy flag is set so this lane cannot see
                // itself; importBusy is still null here, so ExternalEngineBusy reports only a
                // re-transcription and the live engine is State.
                if (comp.Controller.State != LocalScribe.Core.Live.SessionState.Idle)
                    throw new InvalidOperationException(
                        "A live recording is in progress - stop it before importing audio.");
                if (comp.Controller.ExternalEngineBusy?.Invoke() is string engineBusy)
                    throw new InvalidOperationException(
                        $"Another engine is busy ({engineBusy}) - wait for it to finish before importing audio.");

                importLane.BusyReason = "audio import";
                importLane.DetectionOutcome = null;
                try
                {
                    // Phase 1. Task.Run because ImportAsync is CPU-heavy (decode + the offline
                    // whisper pipeline, whose worker loop is NOT self-dispatched) and the dialog VM
                    // awaits this on the UI thread - without it the model load and full-file
                    // transcribe would freeze the dialog and starve Cancel on a long jail-call
                    // import.
                    string id = await Task.Run(
                        () => importer.ImportAsync(req, progress, confirm, ct, transcriptProgress), ct);

                    // Phase 2 (design 2026-07-28 approach A). Deliberately AFTER ImportAsync
                    // returned: the session is complete and valid, so a diariser failure can never
                    // reach AudioImporter's delete-the-whole-folder catch, and the Diarised flag is
                    // not clobbered by the Save-stage session.json snapshot window. The busy flag
                    // stays held across this phase so a recording cannot start mid-diarise.
                    //
                    // ct is NOT passed on: cancelling here must abandon detection, never the
                    // completed import - the step reports Cancelled and the session is kept.
                    if (req.SpeakerDetection is not LocalScribe.Core.Import.SpeakerDetection.Off)
                    {
                        progress.Report(LocalScribe.Core.Import.ImportStage.DetectSpeakers);
                        var outcome = await Task.Run(() => detection.RunAsync(
                            id, req.SpeakerDetection, req.SpeakerCount, diariseProgress, ct),
                            CancellationToken.None);
                        importLane.DetectionOutcome = outcome.Result;
                    }
                    return id;
                }
                finally { importLane.BusyReason = null; }
            };
```

- [ ] **Step 3: Pass the availability probe into the VM**

Replace the `ImportDialogViewModel` construction (`:617-619`):

```csharp
            var importVm = new ViewModels.ImportDialogViewModel(decoder, runImport,
                comp.Maintenance, LocalScribe.Core.Transcription.ModelPaths.AvailableModels,
                pickOpenPath, confirmMismatch, errors, dispatch, TimeProvider.System,
                speakerDetectionUnavailable: () => Services.DiarisationAvailability.Probe(
                    LocalScribe.Core.Transcription.ModelPaths.Resolve, diarizerExe));
```

- [ ] **Step 4: Route completion**

Replace the `Completed` handler (`:620-626`):

```csharp
            importVm.Completed += id =>
            {
                _ = sessionsVm.UpsertRowAsync(id);            // in-place row, no scroll jump
                _ = searchIndex.ReindexSessionAsync(id, _shutdownCts.Token);   // newly-imported session is searchable
                _semanticIndex?.Enqueue(id);                  // imported session becomes Related-searchable
                openReadView(id);                             // completion opens the session

                // Detection committed labels, so land the user on the naming step - the clusters
                // hydrate from what was just written, with no second diarisation run (design
                // 2026-07-28 sections 6-7). It opens ON TOP of the read view, so closing it reveals
                // the transcript with the names applied. Every other outcome (one voice, no audio,
                // unavailable, failed, cancelled) already left its own marker and/or a visible
                // refusal, and lands on the read view exactly as before.
                if (importLane.DetectionOutcome == Services.SpeakerDetectionResult.Committed)
                    openSplitSpeakers(id);
                else if (importLane.DetectionOutcome is Services.SpeakerDetectionResult.OneVoice)
                    errors.Info("Speaker detection found only one voice - no speaker labels were applied. "
                                + "Open Split speakers to try a specific number.");
            };
```

> `openSplitSpeakers` is declared at `:313`, far above this point, so the hoisting rule (a lambda cannot reference a local declared later) is satisfied.

- [ ] **Step 5: Build and run the whole suite**

Run: `dotnet build LocalScribe.slnx 2>&1 | tail -10`
Expected: build succeeds. If `ImportRunner` arity errors remain, a fake was missed in Task 8 step 9.

Run: `dotnet test tests/LocalScribe.App.Tests 2>&1 | tail -5`
Expected: PASS, fully green.

Run: `dotnet test tests/LocalScribe.Core.Tests 2>&1 | tail -5`
Expected: PASS except the 2 known fixture failures (`DiarisationFixtureTests`, `GoldenCorpusFixtureTests`) when the private corpora are absent.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.App/App.xaml.cs
git commit -m "feat(import): two-phase import runner with speaker detection

Phase 2 runs AFTER ImportAsync returns, holding the engine-busy flag across
both phases so a recording cannot start mid-diarise. The cancellation token
is NOT forwarded to detection: cancelling there must abandon detection, not
the completed import.

importBusy moves into a volatile ImportLaneState. It was a plain captured
local written on the Task.Run thread and read from SessionController's
StartAsync on another, and its lifetime now spans two phases.

Completion opens Split Speakers on top of the read view when labels were
committed, so the clusters hydrate from what was just written with no
second diarisation run.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YLjXhg7YDw7x6EHfGyUuYk"
```

---

### Task 10: `NotifyRosterChanged` after a Split Speakers confirm

**Files:**
- Modify: `src/LocalScribe.App/App.xaml.cs:342-347` (the `splitVm.DiarisationSaved` handler)
- Test: none — one-line composition wiring with no seam. Verified by build + the manual smoke.

**Why.** `App.xaml.cs:422` wires `detailEditor.Saved += comp.Windows.NotifyRosterChanged;` so an open read view refreshes its speaker names live. The `openSplitSpeakers` lambda has **no equivalent** (`:342-347` reloads any open Session Details editor and refreshes the grid row, but never notifies the read view). So an open read view shows **stale** speaker names after any diarisation confirm.

This is pre-existing, but Task 9 makes it fire on every diarised import: completion opens the read view and then Split Speakers on top of it, so confirming a name leaves the transcript underneath showing `Local Speaker 1`.

- [ ] **Step 1: Add the notification**

```csharp
            splitVm.DiarisationSaved += id =>
            {
                if (sessionDetailsEditors.TryGetValue(id, out var editor))
                    _ = editor.LoadAsync(id, CancellationToken.None);
                _ = sessionsVm.RefreshRowAsync(id);
                // Live roster sync, mirroring detailEditor.Saved += NotifyRosterChanged at :422.
                // Without this an open read view keeps showing the pre-confirm names. Pre-existing,
                // but design 2026-07-28 makes it fire on EVERY diarised import: completion opens
                // the read view and then this dialog on top of it, so confirming a name would leave
                // "Local Speaker 1" on screen underneath. DiarisationSaved is raised only after a
                // persist that did not throw, so this can never notify over a failed confirm.
                comp.Windows.NotifyRosterChanged(id);
            };
```

- [ ] **Step 2: Build**

Run: `dotnet build LocalScribe.slnx 2>&1 | tail -5`
Expected: build succeeds.

Run: `dotnet test tests/LocalScribe.App.Tests 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/LocalScribe.App/App.xaml.cs
git commit -m "fix(speakers): refresh an open read view after a Split Speakers confirm

App.xaml.cs:422 wires NotifyRosterChanged for the details editor but the
openSplitSpeakers lambda had no equivalent, so an open read view kept
showing pre-confirm speaker names. Import completion now opens the read
view and Split Speakers on top of it, which would make this visible on
every diarised import.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YLjXhg7YDw7x6EHfGyUuYk"
```

---

### Task 11: Close all three engine-gate holes

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs` (ctor + `RunAsync`)
- Modify: `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs` (ctor + `BackfillScanAsync`)
- Modify: `src/LocalScribe.App/App.xaml.cs` (one shared probe, wired into both)
- Test: `tests/LocalScribe.App.Tests/DiarisationEngineGateTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: a new **trailing optional** ctor parameter `Func<string?>? engineBusy = null` on both VMs. Null (the test default) means "never refuse", so every existing construction site compiles and behaves exactly as before.

**Why.** Diarisation is entirely outside the one-engine-at-a-time contract. `ExternalEngineBusy` has exactly two writes (`CompositionRoot.cs:112`, `App.xaml.cs:581`) and two reads (`SessionController.cs:391`, `App.xaml.cs:605`), and **no diarisation code touches it**. You can start a Split Speakers run in the middle of a live recording today, with no refusal and no banner. The voiceprint backfill scan (`SettingsPageViewModel.cs:966-977`) does the same, and runs with `CancellationToken.None`.

Contention is CPU/RAM only — the diariser sets no Provider/GPU field (`SherpaDiarisationRunner.cs:20-26`), so there is no VRAM contention with whisper. But CPU theft can spuriously trip whisper's RTF downgrade ladder (`TranscriptionWorker.cs:121-134`), silently dropping a live recording to a smaller model.

**Implement as probe-and-refuse, not a latch.** The contract is a deliberate cooperative probe (`SessionController.cs:168-170`, pinned by `SessionControllerTests.cs:544-566` — "the seam is a probe, not a latch"). Do not add a mutex. Also note the live-recording direction is `Controller.State != Idle`, checked **separately** from `ExternalEngineBusy` (`App.xaml.cs:602` vs `:605`) — the shared probe must cover both.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/DiarisationEngineGateTests.cs`. Copy the `MakeFinalizedSession`/`FakeEngine` harness from `SplitSpeakersViewModelTests.cs`, adding a `Calls` counter to the engine and an `engineBusy` parameter to `MakeVm`:

```csharp
    [Fact]
    public async Task Split_speakers_refuses_to_run_while_an_engine_is_busy()
    {
        // Today you can start a Split Speakers run mid-recording with no refusal and no banner.
        // Contention is CPU/RAM only (the diariser sets no GPU field), but CPU theft can spuriously
        // trip whisper's RTF downgrade ladder at TranscriptionWorker.cs:121-134.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var reporter = new FakeUiErrorReporter();
        var vm = MakeVm(svc, paths, engine, reporter,
            engineBusy: () => "a recording is in progress");
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(0, engine.Calls);
        Assert.Empty(vm.Clusters);
        Assert.Contains(reporter.Infos, m => m.Contains("recording", StringComparison.OrdinalIgnoreCase));
        // Probe-and-refuse, not a fault: the dialog stays usable.
        Assert.Empty(reporter.Reports);
    }

    [Fact]
    public async Task Split_speakers_runs_normally_when_nothing_is_busy()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var reporter = new FakeUiErrorReporter();
        var vm = MakeVm(svc, paths, engine, reporter, engineBusy: () => null);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;
        engine.Next = new DiarisationResult(
            [new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)], 2, "fake");

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(1, engine.Calls);
        Assert.Equal(2, vm.Clusters.Count);
    }

    [Fact]
    public async Task A_null_probe_never_refuses_so_existing_call_sites_are_unaffected()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var vm = MakeVm(svc, paths, engine, new FakeUiErrorReporter(), engineBusy: null);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task The_probe_is_re_read_at_run_time_not_captured_at_construction()
    {
        // A dialog opened while idle must still refuse if a recording starts before Run is pressed.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        string? busy = null;
        var reporter = new FakeUiErrorReporter();
        var vm = MakeVm(svc, paths, engine, reporter, engineBusy: () => busy);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;

        busy = "a recording is in progress";
        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(0, engine.Calls);
    }
```

Then, in a second class (or the same file) for the backfill scan — read `SettingsVoiceprintTests.cs` first for its existing harness and copy it:

```csharp
    [Fact]
    public async Task The_voiceprint_backfill_scan_refuses_while_an_engine_is_busy()
    {
        // SettingsPageViewModel.cs:966-977 runs the same helper over EVERY finished session, with
        // CancellationToken.None, with no engine-busy check at all.
        var vm = MakeSettingsVm(engineBusy: () => "a recording is in progress");

        await vm.BackfillScanCommand.ExecuteAsync(null);

        Assert.Contains("recording", vm.BackfillStatus, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsVoiceprintBusy);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~DiarisationEngineGateTests" 2>&1 | tail -5`
Expected: FAIL — the `engineBusy` ctor parameter does not exist.

- [ ] **Step 3: Gate the Split Speakers run**

In `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs`, add a field beside the others (`:176-188`):

```csharp
    /// <summary>One-engine-at-a-time probe (design 2026-07-28 adjacent fix 3): non-null = a
    /// user-facing reason another heavy engine owns the machine right now. Null (or a null probe)
    /// means run. Probe-and-refuse, never a latch - the seam is deliberately cooperative
    /// (SessionController.cs:168-170, pinned by SessionControllerTests.cs:544-566).</summary>
    private readonly Func<string?>? _engineBusy;
```

Add a **trailing optional** ctor parameter after `enrollment`:

```csharp
        VoiceprintEnrollmentService enrollment,
        Func<string?>? engineBusy = null)
```

and assign it in the body beside the voiceprint triple:

```csharp
        (_people, _loadMatters, _enrollment) = (people, loadMatters, enrollment);
        _engineBusy = engineBusy;
```

At the top of `RunAsync` (`:418-421`), after the `selected.Count == 0` guard and **before** `_cts` is created:

```csharp
        // Refuse rather than contend. Read at RUN time, not construction: a dialog opened while
        // idle must still refuse if a recording started before Run was pressed.
        if (_engineBusy?.Invoke() is string busy)
        {
            _reporter.Info(busy);
            return;
        }
```

- [ ] **Step 4: Gate the backfill scan**

In `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs`, add a field beside `_resolveModel` (`:161`):

```csharp
    private readonly Func<string?>? _engineBusy;
```

Add a trailing optional ctor parameter after `copyMcpSnippetToClipboard` and assign it. Then at the top of `BackfillScanAsync` (`:966-970`), after the existing null-guard and **before** `IsVoiceprintBusy = true`:

```csharp
        if (_enrollment is null || _embeddingEngine is null || _resolveModel is null) return;
        // Same one-engine refusal as the Split Speakers run (design 2026-07-28 adjacent fix 3):
        // this walks EVERY finished session through the diarisation helper.
        if (_engineBusy?.Invoke() is string busy)
        {
            _dispatch(() => BackfillStatus = busy);
            return;
        }
```

- [ ] **Step 5: Wire one shared probe in `App.xaml.cs`**

Declare it **before** `openSplitSpeakers` (which is at `:313`) so the hoisting rule holds — a lambda cannot reference a local declared later:

```csharp
        // One-engine-at-a-time for the sherpa diarisation lane (design 2026-07-28 adjacent fix 3).
        // Covers BOTH directions the codebase keeps separate: a live recording is Controller.State
        // (App.xaml.cs:602's check), while another offline owner is the ExternalEngineBusy func
        // (:605's check). Read live on every call, so a lane registered later (the import chain at
        // :581) is included automatically.
        Func<string?> heavyEngineBusy = () =>
            comp.Controller.State != LocalScribe.Core.Live.SessionState.Idle
                ? "A recording is in progress - stop it before running speaker detection."
                : comp.Controller.ExternalEngineBusy?.Invoke();
```

Pass it into the Split Speakers VM (`:329-335`) as the new trailing argument:

```csharp
                new LocalScribe.Core.People.VoiceprintEnrollmentService(
                    comp.Paths, TimeProvider.System, () => Guid.NewGuid().ToString("N")),
                heavyEngineBusy);
```

and into `SettingsPageViewModel` (near `resolveModel:` at `:254`) as a named argument:

```csharp
            engineBusy: heavyEngineBusy,
```

> The import lane needs nothing here — Task 9 already holds `importLane.BusyReason` across both phases, and `heavyEngineBusy` reads `ExternalEngineBusy` live, so the import chain registered at `:581` is picked up automatically even though it is assigned later in the method.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~DiarisationEngineGateTests" 2>&1 | tail -5`
Expected: PASS, 5 tests.

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SplitSpeakers|FullyQualifiedName~Settings" 2>&1 | tail -5`
Expected: PASS — every existing construction site passes no probe, so nothing refuses.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/DiarisationEngineGateTests.cs
git commit -m "fix(diarisation): join the one-engine-at-a-time contract

Diarisation touched ExternalEngineBusy nowhere, so a Split Speakers run or
a voiceprint backfill scan could start mid-recording with no refusal and no
banner. Contention is CPU/RAM only (the diariser sets no GPU field), but CPU
theft can spuriously trip whisper's RTF downgrade ladder.

Probe-and-refuse, not a latch: the seam is deliberately cooperative
(SessionController.cs:168-170, pinned by SessionControllerTests.cs:544-566).
The probe is read at run time, not construction, and covers both directions
the codebase keeps separate - Controller.State for a live recording and
ExternalEngineBusy for another offline owner.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YLjXhg7YDw7x6EHfGyUuYk"
```

---

### Task 12: `tools/verify-diarizer.ps1` publish guard

**Files:**
- Create: `tools/verify-diarizer.ps1`
- Modify: `docs/plans/2026-07-04-stage-5-smoke-runbook.md` (point the publish step at the new guard)

**Interfaces:** none — a standalone tooling guard.

**Why, and why it is NOT a copy of the assistant guard.** `LocalScribe.Diarizer.exe` is deployed by **no** build step: no `ProjectReference`, no post-build copy, no verify script. `App.csproj:32-38` documents that a same-folder copy would overwrite App's `onnxruntime.dll` (1.22) with sherpa's (1.24.4) and calls it "actively unsafe". Publishing is a hand-run `dotnet publish` documented only in `docs/plans/2026-07-04-stage-5-smoke-runbook.md:46-58`, and `LocalScribe.Diarizer.csproj` carries **zero** publish properties — `PublishSingleFile`, `SelfContained` and `IncludeNativeLibrariesForSelfExtract` exist only as command-line args in that runbook. Nothing in the build enforces any of it. Import now depends on this helper by default.

The Diarizer publish is **self-contained single-file**, so the presence list is one entry. The guard's real job is the **inverse** of the assistant's: assert that `onnxruntime.dll` and `sherpa-onnx-c-api.dll` are **absent** from the App directory. A pure copy of `verify-assistant-publish.ps1` would only check presence and would miss the actual hazard.

Follow the rigid 4-part template shared by all three existing guards: header comment naming the design + the load-bearing mechanism, `param(...)` + `$ErrorActionPreference = 'Stop'`, the checks, then a FAIL block listing misses with a remediation hint and `exit 1`, else a `PASS: ...` line and `exit 0`.

- [ ] **Step 1: Read the templates**

Run: `cat tools/verify-assistant-publish.ps1 tools/verify-mcp-publish.ps1`

Confirm the shape before writing. Note `verify-import-models.ps1` uses `$ModelsDir`/`$name` rather than `$PublishDir`/`$rel` because its list is flat — this guard needs two directories, so it takes two parameters.

- [ ] **Step 2: Write the guard**

Create `tools/verify-diarizer.ps1`:

```powershell
# tools/verify-diarizer.ps1
# Layout guard for the sherpa diarisation helper (design 2026-07-28 section 8, adjacent fix 5).
# Import-time speaker detection depends on this helper BY DEFAULT, but nothing in the build
# deploys it: LocalScribe.Diarizer.csproj carries no publish properties at all, and the
# self-contained single-file flags live only as command-line args in
# docs/plans/2026-07-04-stage-5-smoke-runbook.md.
#
# This guard is the INVERSE of verify-assistant-publish.ps1. The helper ships self-contained and
# single-file (-p:IncludeNativeLibrariesForSelfExtract=true), so the presence list is one entry -
# the real hazard is the opposite direction. LocalScribe.App.csproj:32-38 documents that copying
# the helper's payload NEXT TO the app would overwrite App's onnxruntime.dll (1.22) with sherpa's
# (1.24.4) and calls it "actively unsafe": that collision breaks Silero VAD. So the guard also
# asserts those DLLs are ABSENT from the app directory.
param(
    [Parameter(Mandatory = $true)][string] $PublishDir,
    [Parameter(Mandatory = $true)][string] $AppDir
)
$ErrorActionPreference = 'Stop'

# Present, non-empty, in the helper's OWN publish directory.
$required = @(
    'LocalScribe.Diarizer.exe'
)

# Absent from the APP directory. Their presence is the ORT 1.24.4-over-1.22.0 collision.
$forbiddenBesideApp = @(
    'onnxruntime.dll'
    'sherpa-onnx-c-api.dll'
    'LocalScribe.Diarizer.exe'
)

$missing = @()
foreach ($rel in $required) {
    $p = Join-Path $PublishDir ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path $p) -or (Get-Item $p).Length -eq 0) { $missing += $rel }
}

$collisions = @()
foreach ($name in $forbiddenBesideApp) {
    $p = Join-Path $AppDir $name
    if (Test-Path $p) { $collisions += $name }
}

if ($missing.Count -gt 0) {
    Write-Host "FAIL: diarizer publish at '$PublishDir' is incomplete - missing or empty:"
    $missing | ForEach-Object { Write-Host "  $_" }
    Write-Host "Publish it self-contained and single-file, e.g.:"
    Write-Host "  dotnet publish src/LocalScribe.Diarizer -c Release -r win-x64 --self-contained true \"
    Write-Host "    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <dir>"
    exit 1
}

if ($collisions.Count -gt 0) {
    Write-Host "FAIL: sherpa payload found BESIDE the app binary in '$AppDir':"
    $collisions | ForEach-Object { Write-Host "  $_" }
    Write-Host "This is the ORT collision LocalScribe.App.csproj:32-38 warns about - sherpa's"
    Write-Host "onnxruntime 1.24.4 would load instead of App's 1.22.0 and break Silero VAD."
    Write-Host "The helper must live in its OWN folder, never flattened into the app directory."
    exit 1
}

Write-Host "PASS: diarizer helper present ($($required.Count) required file) and no sherpa payload beside the app."
exit 0
```

- [ ] **Step 3: Run the guard against a real publish**

```bash
dotnet publish src/LocalScribe.Diarizer -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o artifacts/diarizer
pwsh -File tools/verify-diarizer.ps1 -PublishDir artifacts/diarizer -AppDir artifacts/diarizer
```

Expected: `FAIL` on the collision check — the helper's own publish dir naturally contains `LocalScribe.Diarizer.exe`, and passing it as `-AppDir` is wrong. That failure confirms the collision branch works. Now run it correctly:

```bash
pwsh -File tools/verify-diarizer.ps1 -PublishDir artifacts/diarizer -AppDir src/LocalScribe.App/bin/Debug/net10.0-windows
```

Expected: `PASS`.

Then prove the presence branch fails:

```bash
mkdir -p artifacts/empty
pwsh -File tools/verify-diarizer.ps1 -PublishDir artifacts/empty -AppDir src/LocalScribe.App/bin/Debug/net10.0-windows
```

Expected: `FAIL: diarizer publish ... is incomplete`, exit 1.

Clean up: `rm -rf artifacts/empty`.

- [ ] **Step 4: Point the runbook at the guard**

In `docs/plans/2026-07-04-stage-5-smoke-runbook.md`, after the existing `dotnet publish` step (around lines 46-58), add:

```markdown
Verify the layout before smoking anything:

```powershell
pwsh -File tools/verify-diarizer.ps1 -PublishDir <publish-dir> -AppDir <app-dir>
```

This checks both directions: the helper exe is present and non-empty in its own folder, AND no
sherpa payload (`onnxruntime.dll`, `sherpa-onnx-c-api.dll`) has been flattened next to the app
binary, which would load sherpa's ONNX Runtime 1.24.4 over App's 1.22.0 and break Silero VAD.
```

- [ ] **Step 5: Commit**

```bash
git add tools/verify-diarizer.ps1 docs/plans/2026-07-04-stage-5-smoke-runbook.md
git commit -m "chore(diarizer): publish layout guard

Import-time speaker detection depends on LocalScribe.Diarizer.exe by
default, but nothing in the build deploys it - the csproj carries no
publish properties and the single-file flags live only as command-line
args in a runbook.

Deliberately the INVERSE of the assistant guard. The helper ships
self-contained single-file so the presence list is one entry; the real
hazard is the opposite direction, so this also asserts onnxruntime.dll and
sherpa-onnx-c-api.dll are ABSENT beside the app binary - the ORT
1.24.4-over-1.22.0 collision App.csproj:32-38 calls actively unsafe.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YLjXhg7YDw7x6EHfGyUuYk"
```

---

## Final gate (after all tasks)

- [ ] **Build and run all three suites**

```
dotnet build LocalScribe.slnx
dotnet test tests/LocalScribe.Core.Tests
dotnet test tests/LocalScribe.App.Tests
dotnet test tests/LocalScribe.Mcp.Tests
```

Expected: build clean with 0 warnings. `Core.Tests` green except the 2 known fixture failures (`DiarisationFixtureTests`, `GoldenCorpusFixtureTests`) when the private corpora are absent. `App.Tests` and `Mcp.Tests` fully green.

- [ ] **Confirm the model-name de-duplication held**

Run: `grep -rn "3dspeaker_speech_campplus\|pyannote-segmentation" src/ --include=*.cs`
Expected: exactly two hits, both in `src/LocalScribe.Core/Diarisation/DiarisationModels.cs`.

- [ ] **Confirm the engine-gate chain was not clobbered**

Run: `grep -n "ExternalEngineBusy" src/LocalScribe.App/App.xaml.cs src/LocalScribe.App/CompositionRoot.cs src/LocalScribe.Core/Live/SessionController.cs`
Expected: the two writes (`CompositionRoot.cs:112` base, `App.xaml.cs` chain) and the reads, with the chain still capturing `priorEngineBusy` **before** replacing the property.

- [ ] **Whole-branch review**

Per house practice, run a whole-branch review before merging. Every recent round found at least one cross-task seam defect that per-task review missed — the voiceprint round found a 3-readers/zero-writers dead field, the MCP round found a write-on-read hole. Pay particular attention to:
- `SplitSpeakersViewModel` after Tasks 3, 5, 6, 7 and 11 have all edited it.
- Whether `_resultBySource` emptiness is a sound discriminator for the rename-only path in every reachable state (loaded-then-run-then-cancelled, run-then-deselected, two sources where one ran).
- Whether any new field has readers but no writers, or writers but no readers.

## Manual smoke (cannot be unit tested)

1. **Real multi-speaker mono import, end to end** against the real helper. Confirm: the Speakers control defaults to "Detect automatically"; the detect stage shows a determinate bar then "Matching voices..."; Split Speakers opens pre-filled with `Local Speaker 1/2/...`; naming and confirming updates the read view underneath without a reopen. **Record the wall-clock detection time and the audio duration** — this is the first RTF data point this repo has for the diariser, and the spec's follow-up (tuning the `0.5f` threshold) depends on it.
2. **Reopen Split Speakers** on that session and confirm it fills instantly with **no** diarisation run, that renaming persists, and that the diarisation timestamp is unchanged.
3. **Split-stereo import**: the Speakers control is replaced by the channel note and no detection runs.
4. **Unavailable path**: rename `LocalScribe.Diarizer.exe`, reopen the dialog, confirm the control is disabled with its reason and the import completes undiarised.
5. **`AudioRetention = "never"`**: import with detection on and confirm the `NoAudio` marker lands and the import is otherwise fine.
6. **Engine gate**: start a recording, then try Split Speakers and the Settings voiceprint backfill scan — both must refuse with a visible reason.

## Out of scope (from the spec, restated so nobody adds them mid-round)

- The `Win32Exception` fix at `ProcessDiarisationHelper.cs:33`. The pre-flight gate plus Task 5's broad catch make it survivable; the documented-but-fictional `HELPER_CRASH` contract in spec section 8.2 stays wrong for now.
- Tuning the `0.5f` auto threshold or recording a real-audio DER corpus. Smoke step 1 produces the first data.
- Diarising split-stereo legs; a cluster-merge affordance; the dead `Speakers.Confidence` field; `speakers.DiarisedSources` being read by no UI; a helper timeout/watchdog; background (non-modal) import.

