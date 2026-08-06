# Tier 1D input: where components live, and how they get there

Date: 2026-08-06
Status: design note, feeding T1-10 of `2026-08-05-tier1-hardening-design.md`
Trigger: found during the Tier 1C smoke run - the Import audio/video button came up greyed out in
the worktree because `FfmpegLocator` never reaches the main repo's `tools\ffmpeg`. That is a dev
inconvenience. The same shape shipped to a stranger is a broken install, and this note exists so
1D designs the landing place rather than discovering it.

## Measured payload (2026-08-06, this machine)

| Component | Size | Notes |
|---|---|---|
| ffmpeg, shared build | 144.6 MB | 127.5 MB without `ffplay.exe`, which nothing probes for |
| Whisper `ggml-*` | 5.1 GB | 10 files, f16 + quantized variants |
| Assistant GGUFs | 7.1 GB | 4 files; largest single file 3.19 GB |
| Diarisation `.onnx` + CAM++ | 63 MB | sherpa segmentation + speaker embedding |
| **`models\` total** | **12.0 GB** | 31 files |

GitHub release assets cap at 2 GB each. `gemma-4-E2B_q4_0-it.gguf` alone is 3.19 GB, so it cannot be
a release asset at all in its current form, and the library as a whole is six times the cap. **A
fully-offline installer is not achievable on this distribution channel.** That is settled by
arithmetic, not preference.

## Ruling that constrains everything below

The T1-10 constraint is non-negotiable and still holds: a grep for
`System.Net|HttpClient|Socket|WebRequest|Dns` across all eight projects returns **zero matches**
(re-verified 2026-08-06). A solicitor can establish that this app cannot phone home without reading
a line of logic, and that mechanical checkability is the product's most valuable privacy asset.

Therefore **any** component downloader lives in a separate helper executable spawned on explicit
user action over stdio, following the existing child-process pattern. An in-process `HttpClient` is
rejected regardless of convenience, and "just this once, it's only for models" is the exact
reasoning that would destroy the property.

## Decision 1: bundle what is fixed, fetch what is chosen

**Bundle in the installer:** ffmpeg (127 MB, minus `ffplay.exe`) and the diarisation models (63 MB).
Neither is a user choice - the app cannot import audio without the first or split speakers without
the second - so there is no consent question to ask and no reason to make someone wait on a download
for them. ~190 MB is affordable in a release asset.

**Fetch in-app, per component, on explicit action:** Whisper weights and the assistant GGUFs. Both
are genuine choices - a user who only ever transcribes English live calls needs neither
`large-v3-turbo` nor a 3.2 GB chat model - and bundling them is impossible anyway.

REJECTED: fetching ffmpeg too, for uniformity. It converts a guaranteed-present dependency into a
runtime failure mode, and the greyed-out Import button is a worse first impression than 127 MB of
installer.

REJECTED: a separate "full" installer carrying everything. It cannot exist on GitHub releases, and
maintaining a second distribution channel to dodge a cap is disproportionate.

## Decision 2: one component root, and it is the app's own directory

Five components already anchor at `AppContext.BaseDirectory`, independently and by convention rather
than by contract:

| Component | Probe |
|---|---|
| ffmpeg | `<base>\ffmpeg\` (`FfmpegLocator`, probe 2) |
| Whisper models | `<base>\models\` (`ModelPaths`, final fallback) |
| Assistant helper | `<base>\` (`AssistantHelperLocator`) |
| MCP server | `<base>\LocalScribe.Mcp.exe` |
| Diarizer helper | path composed at `CompositionRoot` |

Make that explicit rather than incidental: the installed layout is the app directory with `ffmpeg\`,
`models\`, `assistant\` and the helper exes as siblings, and the acquisition helper writes into the
same root. Every locator's env-var override stays, because that is what makes a worktree, a test
fixture and a portable install work.

## Decision 3: fix the two locator defects BEFORE packaging depends on them

Both locators carry dev-only probes that will cheerfully mask a broken install on a developer's
machine. This is not hypothetical - ffmpeg has been resolving via the repo walk-up for months, so
nothing has ever exercised the shipping path.

**(a) `ModelPaths` returns its walk-up result unconditionally.**

```csharp
for (var d = dir; d is not null; d = d.Parent)
    if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx")))
        return Path.Combine(d.FullName, "models");     // never checks it EXISTS
return Path.Combine(AppContext.BaseDirectory, "models");
```

The first `.slnx` above the binary wins even when its `models\` is absent, so the final
beside-the-binary fallback is unreachable whenever any `.slnx` is an ancestor. That is why the
worktree failure reads *"Model 'small.en' is not downloaded"* rather than falling through. Fix:
fall through when the directory does not exist, matching `FfmpegLocator`, which already validates
(`HasTools(repoTools) ? repoTools : null`).

**(b) The two locators disagree about probe order.** `FfmpegLocator` checks beside-the-binary BEFORE
the repo walk-up; `ModelPaths` checks the walk-up first. On an installed machine there is no `.slnx`
above the exe so both land in the same place, but the inconsistency is a trap for the next person.
Settle on one order - env, then beside-the-binary, then repo walk-up - so the SHIPPING path is the
one exercised first everywhere and the dev convenience is the fallback rather than the default.

## Decision 4: the published-layout test is the deliverable, not the downloader

The downloader is ordinary work. The thing that actually prevents a packaging regression reaching a
stranger is a test that runs every locator against a **published output directory with no `.slnx`
anywhere above it** and asserts each component resolves. Without it, the dev probes guarantee a
green suite on a machine where the install layout is wrong.

It belongs in CI, over the real `dotnet publish` output, not over `bin\Debug`.

## Decision 5: the acquisition manifest, and why sha256 is mandatory

`models\assistant-manifest.json` already has the right shape - schema-versioned, per-file
`sha256`, `license`, `role`, `nativeCtx`. Generalise it to cover Whisper and sherpa rather than
inventing a second mechanism.

The hash check is **not** an integrity nicety. It is the same argument Tier 1C just made about
`manifest.json`: a model that downloaded wrong produces a different transcript, and nothing
downstream would ever say so. A truncated or substituted weights file is indistinguishable from a
worse model at the point of use. Verify before the file is moved into place, and refuse rather than
degrade.

The `license` field is already carried and should surface in the UI at download time - shipping
Gemma weights silently is a licensing question, not a technical one.

## What the user has to be told, and when

Per component, before any download starts: what it is for, how large it is, and its licence. A
progress UI that can be cancelled, and a partial file that is discarded rather than resumed into
place. The existing degrade pattern is the model for the absent state - the button stays visible and
disabled with a tooltip naming the remedy, exactly as Import does today.

Live capture's default models (`small.en` / `base.en`, 78-465 MB depending on quantization) are the
smallest useful fetch and should be what first-run offers. `large-v3-turbo` is the import default and
is 1.5 GB (547 MB quantized) - offer it, do not assume it.

## Out of scope for 1D

- An auto-updater. Same zero-network constraint, same helper-exe answer, but a separate decision
  with its own consent question.
- Mirroring or self-hosting the weights. The upstream sources are already the trust anchor; adding a
  mirror adds a supply-chain link without removing one.
- Bundling a static ffmpeg build to drop the DLLs. Plausibly ~40 MB smaller, but unmeasured here and
  it changes the licence posture (GPL vs LGPL depending on build flags). Measure before proposing.
