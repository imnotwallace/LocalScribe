# Stage 5 smoke runbook - Split speakers (diarisation) on real hardware

Prereqs: app models fetched (`pwsh tools/fetch-models.ps1` - now also fetches the two
diarisation models, pyannote-segmentation-3.0 and 3D-Speaker CAM++); Stage 4 C-series
previously passed on this box; at least one real Webex session with a declared Remote
participant count > 1 (bump `Remote count` in the metadata editor on an existing multi-speaker
recording if none exists yet), finalized, with its Remote leg retained.

Known limitation carried into this runbook: the `afterDiarisation` audio-retention seam is
specified (§7 of the specs) but **not wired** in this delivery - the no-delete firewall means
none of D1-D7 should ever observe audio being deleted, regardless of the retention setting.

## Prerequisite

**Publish `LocalScribe.Diarizer.exe` beside the app before D1-D6.** This is a hard blocker for
every dialog-based check below (D1-D6; D7 publishes its own ARM64 copy). Split speakers is
wired to resolve `LocalScribe.Diarizer.exe` from
`AppContext.BaseDirectory` (`CompositionRoot.Build()`), but neither `dotnet run`/`dotnet build`
on `LocalScribe.App` nor any MSBuild step places it there automatically - production packaging
of the helper is Stage 7's job (see `LocalScribe.App.csproj`'s long Task 9 comment and the
README's getting-started note). Without it, the dialog opens fine but any Run fails with a
`HELPER_CRASH` (harmless - it will not corrupt anything, but D1-D6 cannot proceed).

**Why this needs care, not just a file copy:** `LocalScribe.Diarizer` carries its own
`org.k2fsa.sherpa.onnx` package, which ships its own `onnxruntime.dll` **1.24.4**. The app's own
Whisper/Silero path loads `Microsoft.ML.OnnxRuntime` **1.22.0**'s `onnxruntime.dll` from the same
relative output path (`runtimes/win-x64/native/`). A plain folder publish of the Diarizer
(self-contained or framework-dependent, without `PublishSingleFile`) drops a loose 1.24.4
`onnxruntime.dll` in its output folder - copying that whole folder flat into the app's own bin
directory **overwrites** the app's 1.22.0 DLL with the incompatible 1.24.4 build and breaks
Silero VAD on the live capture path. This was tried and rejected empirically while building Task
9 (see `CompositionRoot.Build()`'s comment). The safe shape is a **self-contained,
single-file** publish **with `-p:IncludeNativeLibrariesForSelfExtract=true`**: `PublishSingleFile`
alone only bundles managed dependencies - native libraries such as `onnxruntime.dll` and
`sherpa-onnx-c-api.dll` are still extracted LOOSE beside the produced `.exe` unless
`IncludeNativeLibrariesForSelfExtract` is also set. With that flag, every managed AND native
dependency is bundled **inside** the one `.exe`, which extracts them to a private per-process temp
cache at run time rather than dropping them as loose sibling files. Only that one `.exe` is ever
copied next to the app - and that is only safe once this flag makes it true that nothing native
was left loose in the scratch folder next to it.

```powershell
# 1. Publish the helper, self-contained + single-file + native-libs-bundled, to a SCRATCH folder
#    (never the app's own output folder directly - the scratch folder also contains a .pdb and
#    other loose files that must NOT be copied).
dotnet publish src/LocalScribe.Diarizer -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o C:\temp\diarizer-publish

# 2. Copy ONLY the single .exe into LocalScribe.App's own build output folder (the default
#    `dotnet run --project src/LocalScribe.App` Debug config output shown below - adjust if you
#    build Release instead). Do NOT copy the rest of the scratch folder's contents. (Without step
#    1's IncludeNativeLibrariesForSelfExtract flag, onnxruntime.dll/sherpa-onnx-c-api.dll would sit
#    loose in the scratch folder next to the exe - copying "the whole publish folder" as a
#    workaround would then drop sherpa's ORT 1.24.4 into App's own bin and overwrite the Microsoft
#    ORT 1.22.0 Silero VAD loads in-process. The flag above makes "copy ONLY the .exe" both
#    sufficient and safe.)
Copy-Item C:\temp\diarizer-publish\LocalScribe.Diarizer.exe `
  src\LocalScribe.App\bin\Debug\net10.0-windows\LocalScribe.Diarizer.exe
```

Verify before starting D1: launch the app (`dotnet run --project src/LocalScribe.App`), open a
splittable session's read view, click "Split speakers...", select a source, click Run - it
should show a progress bar and NOT immediately fail with a helper-crash error. If it does,
re-check step 2 copied the exe to the exact folder the running app's `AppContext.BaseDirectory`
resolves to (a stale exe from a previous build config is the most common miss).

Run: `dotnet run --project src/LocalScribe.App`

## D1 - Split a real multi-remote-speaker call (Webex primary use case)

Steps: open the read view for the prepared multi-speaker Webex session; click "Split
speakers..." (read-view toolbar, or the Sessions-page context menu); select the Remote source;
click Run.
Expected: the dialog shows an in-dialog progress bar while running; on completion each detected
cluster gets its own row with a few representative transcript lines and a play-snippet button;
default names read "Remote Speaker 1", "Remote Speaker 2", ... (1-based, per-side). Confirm ->
`speakers.json` is written for the session, the session gains its `Diarised` badge, and the read
view re-renders with the new per-speaker names replacing the "Them" baseline label.
Record: pass/fail + detected cluster count vs the declared Remote count + confirm
`speakers.json.diarisedSources` includes `"Remote"`.

## D2 - Cancel mid-run leaves the session unchanged

Steps: hash the session's truth files first: `Get-FileHash sessions\<id>\speakers.json` (if it
exists yet) and note whether the `Diarised` badge is currently shown. Start a Run, click Cancel
partway through (before the progress bar completes). Also watch for the helper process:
`Get-Process LocalScribe.Diarizer` during the run.
Expected: the dialog returns to its pre-run state (no partial cluster list, Run re-enabled); no
`speakers.json` is written (or, if one existed, it is byte-identical - re-hash to confirm); the
`Diarised` badge does not flip; `Get-Process LocalScribe.Diarizer` shows the process gone shortly
after Cancel (killed with its process tree, not orphaned).
Record: pass/fail + before/after hash + confirmation the helper process actually exited.

## D3 - Name-by-snippet playback

Steps: in the naming step of a completed run (from D1), click each cluster's play button in
turn; type a new name into one cluster's name field without clicking Confirm yet.
Expected: each play click seeks and plays a short snippet from that cluster's earliest diarised
segment on the correct (Remote) leg - audibly the right speaker; playback works without closing
the dialog; the typed name shows immediately in the row but is NOT persisted to
`speakers.json` until Confirm is clicked (close the dialog without confirming and re-open to
verify the rename did not stick).
Record: pass/fail per cluster played + confirmation the un-confirmed rename did not persist.

## D4 - Re-diarise preserves a pinned reassignment (and the collision case)

Steps: on the already-split session from D1, pin one specific segment to a different speaker via
whatever surface exposes a per-segment reassignment (read-view speaker reassignment). Confirm
it, then hash/record that segment's `assignments`/`pinned` entry and the name of the clusterKey
it points to in `speakers.json`. Re-run Split speakers on the same (Remote) source and Confirm
the fresh run.
Expected: after re-diarise, the pinned seq keeps its exact prior clusterKey and name -
byte-identical in `speakers.json.assignments.Remote.<seq>` and the corresponding `names` entry;
every OTHER (non-pinned) segment on that source takes the fresh run's assignments/names (no
stale name bleeds through from before the re-run); no non-pinned `names` entry for that source
survives untouched. **Collision case:** because fresh cluster ids always restart at 0, check
whether the fresh run's raw output would have reused the pinned clusterKey's numeric id - if so,
verify in `speakers.json` that the fresh cluster was remapped to a different id and the pinned
speaker's name was never overwritten (if the fresh run didn't happen to collide this run,
note that and consider forcing a specific cluster count to provoke it).
Record: pass/fail + before/after `speakers.json` snippets for the pinned seq + whether the
collision case was actually exercised this run.

## D5 - System-mix banner + force-N suppressed

Steps: use a session whose Remote leg fell back to system-mix (a Teams session, or a
browser-based call that hit the all-zeros/browser guard) with a declared Remote count > 1. Open
Split speakers and Run that source.
Expected: a system-mix banner is visible in the dialog for that source; if the auto cluster
count differs from the declared count, the "Use N speakers" force button is **absent or
disabled** for it - contrast directly with D1's clean per-process Webex leg, where the force
button IS offered on a mismatch.
Record: pass/fail + banner text shown + force-button state (present/absent) + the auto vs
declared counts.

## D6 - Missing-model path shows the MODEL_DOWNLOAD_FAILED / fetch hint

Steps: close the app; temporarily rename the segmentation model folder aside
(`models\sherpa-onnx-pyannote-segmentation-3-0` -> `...-aside`); relaunch and open Split
speakers on any splittable session; Run.
Expected: a clear error surfaces (`MODEL_DOWNLOAD_FAILED`) with a hint pointing at
`pwsh tools/fetch-models.ps1`; no partial `speakers.json` is written; the session's existing
data is untouched. Restore the model folder (`...-aside` -> original name) and re-run Split
speakers to confirm it now succeeds again.
Record: pass/fail + exact error text shown + confirmation Split worked again after restoring
the model.

## D7 - win-arm64 run

Steps: on a Windows ARM64 device (or a box you can side-load a cross-published ARM64 build
onto), publish the ARM64 helper: `dotnet publish src/LocalScribe.Diarizer -c Release -r
win-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
-o <scratch>`, copy the single `.exe` beside an ARM64 build/publish of `LocalScribe.App` the same
way as the x64 prerequisite above, and repeat a basic D1-style split on a short multi-speaker leg.
Expected: the ARM64-published `LocalScribe.Diarizer.exe` runs `sherpa-onnx` successfully
(`org.k2fsa.sherpa.onnx` ships an ARM64 native runtime) and produces the same shape of result as
the x64 run - no x64-only crash, no missing-native-library failure.
Record: pass/fail + device used + any ARM64-specific issues observed.

Record results (pass/fail + notes) inline here, per run, dated.

---

## Results

(none yet)
