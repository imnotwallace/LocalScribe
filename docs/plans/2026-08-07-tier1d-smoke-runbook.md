# Tier 1D smoke runbook - reachability, citations, and the first installer

Date: 2026-08-07
Branch: `feat/tier1d-reachability-2026-08-06`
Covers: T1-5 (reachability), T1-9 (selectable text + Copy with citation), T1-10 (packaging)

A static suite cannot settle any item below. Items 6 and 8 are **already done** and recorded
here with their results; the rest need a human at a real machine.

---

## Setup traps - read before you start, these cost time in B, C and D

- **Running from a worktree needs BOTH env vars set before launch.** A worktree has its own
  `.slnx` and neither `models\` nor `tools\ffmpeg\`:
  ```powershell
  $env:LOCALSCRIBE_MODELS = 'F:\LocalScribe\models'
  $env:LOCALSCRIBE_FFMPEG = 'F:\LocalScribe\tools\ffmpeg'
  ```
  `ImportDialogViewModel` probes ffmpeg ONCE at construction, so setting the variable after the
  app is up does nothing.
- **Launch detached.** `dotnet build` then `Start-Process`, never `dotnet run` - the latter stays
  attached as a parent and dies with the shell, taking the app down mid-smoke.
- **Do not test the installed build and a worktree build in the same session.** The installed app
  at `%LOCALAPPDATA%\LocalScribe\current` has its own `models\` and `ffmpeg\`; a stray
  `LOCALSCRIBE_MODELS` in the environment overrides them and you will be testing the wrong tree.
  That override is deliberate (it is what makes a worktree work) and it is exactly why the
  published-layout test copies the tree OUT of the repo before probing.
- **A running `LocalScribe.App.exe` locks `Core.dll`** and the next build dies with `MSB3027`,
  which reads like a compile error and is not one. Close that specific PID. Never blanket-kill.
- **`build.ps1` needs `-ModelsDir` / `-FfmpegDir`** (or the env vars) when run from a worktree.

---

## 1. Dialog owner - T1-5, Task 2

`Application.MainWindow` was never assigned, so WPF auto-assigned the first `Window` constructed -
the `OverlayWindow` recording pill - and all three `CenterOwner` dialogs centred on it.

- [ ] Launch cold. Do **not** open the manager window. Start a recording so the pill is up, then
      open Export from the Record console. It must centre on the shell if the shell is open, and
      on screen otherwise - **never on the pill**.
- [ ] Open the manager window, then close it, then open Export again from a read view. It must
      open, **not throw**. (A closed `Window` left as `Owner` makes `ShowDialog` throw
      `InvalidOperationException` - the clear-on-close half is what this checks.)

## 2. Notice severity - T1-5, Task 1

`MainWindow.xaml` hardcoded `Severity="Error"` and `SyncInfoBar` never re-set it, so all 32
`Info(...)` call sites - including "Exported to ..." - rendered red.

- [ ] Export a session. The "Exported to ..." bar must be **green**, not red.
- [ ] Force a failure (pick a read-only folder). The next bar must be **red**.

## 3. A failed Start is visible - T1-5, Task 6

The only live notice surface was a tray balloon, which Focus Assist suppresses outright.

- [ ] **Turn Focus Assist ON** - this is the point of the item; the balloon must be suppressed so
      the bar is the only surface.
- [ ] Unplug or disable the pinned microphone, press Record.
- [ ] The console must show a **red notice bar** naming the reason. Before this round the Record
      button simply did nothing.
- [ ] Dismiss the bar, press Record again, confirm the SAME message re-opens it. (`RaiseNotice`
      nulls the text first precisely because `[ObservableProperty]` equality-gates a same-value
      set; without that the bar stays shut on a repeat.)

## 4. Cancel an export - T1-5, Task 3

All four export calls passed `CancellationToken.None`, so a multi-gigabyte zip could not be
stopped.

- [ ] Export a long session as `.zip`. Press **Stop** mid-write.
- [ ] Confirm the dialog says "Export cancelled - no file was written." and is **not** red
      (cancelling is a user action, not a fault).
- [ ] Confirm **no partial file** is left at the destination.

## 5. Copy with citation - T1-9, Task 8

- [ ] Select three turns in a read view with Ctrl+click. Press **Ctrl+C** with the list focused.
      Confirm the plain text arrives on the clipboard.
      **This is the item most worth doing.** An `InputBinding` is a `Freezable` in neither the
      visual nor the logical tree; a mis-resolved command binding fails **silently** and the
      gesture just does nothing. No test in this suite can catch a dead gesture.
- [ ] Press **Ctrl+Shift+C**, paste into Word. Every quotation must be attributed, **in transcript
      order** (Ctrl+click bottom-up and confirm the paste is still top-down), with the correct
      transcript version.
- [ ] Right-click a row that is **outside** the current selection. Copy must take the clicked row
      only, not the invisible selection.
- [ ] Scroll a 2-hour transcript top to bottom. Confirm scrolling is no slower than before -
      `SelectionMode="Extended"` must not have cost virtualisation.

## 6. Install - T1-10, Task 13 - **PARTLY DONE 2026-08-07**

**Already verified on this machine:**
- `build.ps1` produced `LocalScribe-win-Setup.exe` (1.24 GB), all ten steps green, every
  `verify-*.ps1` layout guard passing.
- The setup **installed** to `%LOCALAPPDATA%\LocalScribe` and **launched the app** (exit code 0).
- Installed `current\` carries `ffmpeg\` (both required exes, `LICENSE.txt`, **no** `ffplay.exe`),
  `models\` with both manifests and the tiny/base weights, `assistant\`, `mcp\`, `runtimes\` and
  all three helper exes - with **no `.slnx` anywhere above it**, so the dev walk-up is genuinely
  unreachable and the app resolves components via the shipped path.

**Still to do by hand:**
- [ ] On a **clean machine** (not this one - this one has the dev models and env vars).
- [ ] The app launches and records.
- [ ] **Split Speakers runs** - proves `LocalScribe.Diarizer.exe` resolved beside the app and its
      natives were bundled into the single file.
- [ ] **Import works** - proves the bundled `ffmpeg\` is found. This is the item the whole
      packaging design note exists for; the first installer built from the plan had no `ffmpeg\`
      at all and Import would have been dead.
- [ ] Settings -> Components: the **Assistant helper** row must read NOT installed, with the
      detail naming the missing model. The installer deliberately ships the helper without its
      ~2.5 GB weights.
- [ ] Download "Assistant model (Qwen3-4B-Instruct-2507 Q4_K_M)" from that panel, press Refresh,
      confirm the row flips to Installed, and only THEN confirm the assistant answers - which also
      proves `assistant-manifest.json` shipped (without it a downloaded GGUF loads as "no models
      installed").
- [ ] Confirm the licence line is visible on each downloadable row **before** pressing Download.
      The embedding model reads "Gemma Terms of Use", not an OSS licence - that is the disclosure
      the design note required.

## 7. Download a component - T1-10, Task 12

- [ ] With `large-v3-turbo` absent, open Settings -> Components, press **Download**, confirm
      progress advances.
- [ ] **Kill the network mid-download.** Confirm the failure is reported (not silent).
- [ ] Press **Download** again. Confirm it **RESUMES** rather than restarting from zero - watch
      the percentage start well above 0.

**Installed means PRESENT, not VERIFIED.** `ComponentProbe` is a presence-and-size probe, so
corrupting a downloaded file still reads as installed after Refresh - and because `CanDownload` is
`Pin is not null && !Installed && !IsDownloading`, that row then offers no Download button at all.
**Do not try to test corruption recovery from the UI; it is unreachable.** A one-byte edit also
leaves the length unchanged, so even an invoked command would hit the helper's
`have >= ExpectedBytes` short-circuit and skip the transfer. The fail-closed hash check is
exercised by the interrupted download above and pinned by `ComponentFetchClientTests`. A
"Reinstall" affordance is the Tier-2 follow-up.

## 8. The zero-network grep, by hand - T1-10, Task 9 - **DONE 2026-08-07: PASS**

```
git grep -nE "System\.Net|HttpClient|Socket|WebRequest|Dns" -- src/LocalScribe.App src/LocalScribe.Core
```

Result: **zero matches** (exit 1). `git grep -l` over `src/LocalScribe.Fetch` returns exactly one
file, `Program.cs` - the only project in the solution permitted to touch the network.

This is the product's central privacy claim and it is checkable in one command by someone who
does not trust the test.

---

## What this round could NOT settle, and why

- **Signing.** No code-signing certificate exists, so every build so far is UNSIGNED and Windows
  SmartScreen will warn each user. `build.ps1` degrades loudly rather than failing, and takes
  `-CertThumbprint` or `LOCALSCRIBE_SIGN_THUMBPRINT` when a certificate is obtained. Owner action.
- **A clean-machine install.** Everything above was verified on the development machine, which has
  the dev models, the env overrides and the full toolchain. The install layout was checked
  structurally and by the published-layout test, but "works on a machine that has never seen this
  project" is not something this machine can prove.
- **Real-call behaviour.** No item here records a real Webex call; the Tier 1C smoke items still
  own that.
