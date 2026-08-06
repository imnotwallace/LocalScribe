# build.ps1 - the whole shippable build (Tier 1 plan D, T1-10, 2026-08-05).
#
# Publishes the four processes in the ONE order that works, runs every existing
# tools/verify-*.ps1 layout guard as a gate, bundles the small models, and packages the result
# with Velopack. Signing is optional and degrades LOUDLY rather than failing, so this script
# works in CI and on a machine with no certificate.
#
#   .\build.ps1                                   # unsigned, tiny+base models
#   .\build.ps1 -CertThumbprint <40 hex>          # signed
#   .\build.ps1 -WithLargeModels                  # also bundle + verify large-v3-turbo/medium.en
#   .\build.ps1 -SkipTests                        # local iteration only; CI never passes this
param(
    [string] $Configuration = 'Release',
    [string] $OutDir = (Join-Path $PSScriptRoot 'publish'),
    # Falls back to the env var so CI can supply it as a secret without it reaching a command line.
    [string] $CertThumbprint = $env:LOCALSCRIBE_SIGN_THUMBPRINT,
    # Where the bundled model files are read FROM. Defaults to LOCALSCRIBE_MODELS, then the repo's
    # own models\. Added 2026-08-06 after running this script from a git worktree, which has no
    # models\ of its own - the 12 GB library is not duplicated per worktree, and every locator in
    # the product already honours exactly this env var for exactly this reason (packaging design
    # note, decision 2). Without it a worktree build dies at step 8 on nine "missing" files that
    # are all present a directory away.
    [string] $ModelsDir = $(if ($env:LOCALSCRIBE_MODELS) { $env:LOCALSCRIBE_MODELS } else { Join-Path $PSScriptRoot 'models' }),
    # Where the bundled ffmpeg/ffprobe tools are read FROM, same fallback shape as -ModelsDir.
    [string] $FfmpegDir = $(if ($env:LOCALSCRIBE_FFMPEG) { $env:LOCALSCRIBE_FFMPEG } else { Join-Path $PSScriptRoot 'tools\ffmpeg' }),
    [switch] $WithLargeModels,
    [switch] $SkipTests
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$rid = 'win-x64'
$appDir   = Join-Path $OutDir 'app'
$stageDir = Join-Path $OutDir 'stage'
$relDir   = Join-Path $OutDir 'releases'

function Step($text) { Write-Host ""; Write-Host "=== $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "FAIL: $text" -ForegroundColor Red; exit 1 }

# A running LocalScribe.App.exe LOCKS Core.dll and the build dies with MSB3027, which reads like a
# compile error and is not one. Say so plainly rather than letting the user guess. Never kill it -
# that is a standing rule in this repo, and the user may be recording.
$running = Get-Process -Name 'LocalScribe.App' -ErrorAction SilentlyContinue
if ($running) {
    Fail "LocalScribe.App.exe is running (PID $($running.Id -join ', ')) and holds a lock on Core.dll. Close it and re-run."
}

Remove-Item -Recurse -Force $OutDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $appDir, $stageDir, $relDir | Out-Null

Step "1/10 build"
dotnet build (Join-Path $repo 'LocalScribe.slnx') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { Fail "solution build failed" }

Step "2/10 test (model-free gate)"
if ($SkipTests) {
    Write-Host "  SKIPPED by -SkipTests - never use this for a build you intend to ship."
} else {
    dotnet test (Join-Path $repo 'LocalScribe.slnx') -c $Configuration --filter "Category!=Fixture" --nologo
    if ($LASTEXITCODE -ne 0) { Fail "the model-free suite is not green - nothing is published" }
}

Step "3/10 publish app"
dotnet publish (Join-Path $repo 'src\LocalScribe.App') -c $Configuration -r $rid --self-contained true -o $appDir --nologo
if ($LASTEXITCODE -ne 0) { Fail "app publish failed" }

Step "4/10 publish diarizer (single-file, self-contained) and gate it"
$diarStage = Join-Path $stageDir 'diarizer'
# IncludeNativeLibrariesForSelfExtract is LOAD-BEARING, not an optimisation: PublishSingleFile
# alone still drops onnxruntime.dll and sherpa-onnx-c-api.dll LOOSE beside the exe, and copying
# those next to the app would shadow App's own ORT 1.22 with sherpa's 1.24.4 and break Silero VAD
# (LocalScribe.App.csproj's long comment calls that "actively unsafe").
dotnet publish (Join-Path $repo 'src\LocalScribe.Diarizer') -c $Configuration -r $rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $diarStage --nologo
if ($LASTEXITCODE -ne 0) { Fail "diarizer publish failed" }

# Gate BEFORE the copy: verify-diarizer.ps1 asserts the sherpa payload is ABSENT from the app
# directory, so it can only pass while that directory is still clean.
& (Join-Path $repo 'tools\verify-diarizer.ps1') -PublishDir $diarStage -AppDir $appDir
if ($LASTEXITCODE -ne 0) { Fail "diarizer layout guard failed" }

# The half that guard cannot express: prove the single-file publish bundled its natives instead of
# leaving them loose. Anything RUNTIME-LOADABLE beyond the exe here IS the collision.
#
# .pdb and .lib are excluded, and .lib is a correction MEASURED by running this script
# (2026-08-06): the ONNX Runtime package copies onnxruntime.lib and
# onnxruntime_providers_shared.lib - two 2 KB LINK-TIME import libraries for native C++ consumers -
# into the publish output. Nothing loads them at runtime, and the same publish emitted ZERO loose
# .dll files, so IncludeNativeLibrariesForSelfExtract had plainly taken effect. Failing the build
# on them would have made this gate unusable and invited someone to delete it. The check still
# catches every runtime-loadable stray, which is the hazard it exists for.
$stray = Get-ChildItem $diarStage -File |
    Where-Object { $_.Name -ne 'LocalScribe.Diarizer.exe' -and $_.Extension -notin '.pdb', '.lib' }
if ($stray) {
    Fail ("the diarizer publish left loose files beside the exe - IncludeNativeLibrariesForSelfExtract " +
          "did not take effect: " + ($stray.Name -join ', '))
}

$diarizerExe = Join-Path $diarStage 'LocalScribe.Diarizer.exe'
Copy-Item $diarizerExe -Destination $appDir -Force   # CompositionRoot resolves it beside the app

Step "5/10 publish assistant (FOLDER) and gate it"
$assistantDir = Join-Path $appDir 'assistant'
# A FOLDER publish, deliberately not single-file: LLamaSharp probes its own
# runtimes/<rid>/native/<variant>/ layout relative to the helper's directory, and a single-file
# self-extract lands the natives where that probe never looks - which is how the first deployment
# of this helper shipped broken.
dotnet publish (Join-Path $repo 'src\LocalScribe.Assistant') -c $Configuration -r $rid --self-contained true -o $assistantDir --nologo
if ($LASTEXITCODE -ne 0) { Fail "assistant publish failed" }
& (Join-Path $repo 'tools\verify-assistant-publish.ps1') -PublishDir $assistantDir
if ($LASTEXITCODE -ne 0) { Fail "assistant layout guard failed" }

Step "6/10 publish mcp and gate it"
$mcpDir = Join-Path $appDir 'mcp'
dotnet publish (Join-Path $repo 'src\LocalScribe.Mcp') -c $Configuration -r $rid --self-contained true -o $mcpDir --nologo
if ($LASTEXITCODE -ne 0) { Fail "mcp publish failed" }
& (Join-Path $repo 'tools\verify-mcp-publish.ps1') -PublishDir $mcpDir
if ($LASTEXITCODE -ne 0) { Fail "mcp layout guard failed" }

Step "7/10 publish the component fetch helper"
$fetchStage = Join-Path $stageDir 'fetch'
# IncludeNativeLibrariesForSelfExtract is LOAD-BEARING here for the same reason it is at step 4:
# PublishSingleFile ALONE leaves a self-contained publish's native dependencies LOOSE beside the
# exe, and only the exe is copied out of the staging folder below. Without the flag the shipped
# helper cannot start, EVERY Download fails, and the user sees "The download helper exited with
# code N" from ComponentFetchClient with nothing to act on.
dotnet publish (Join-Path $repo 'src\LocalScribe.Fetch') -c $Configuration -r $rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $fetchStage --nologo
if ($LASTEXITCODE -ne 0) { Fail "fetch helper publish failed" }

# Same stray-file assertion as step 4, and for the same reason: only the exe is copied out, so
# anything RUNTIME-LOADABLE left in the staging folder is a file the shipped helper will look for
# and not find. Same measured .pdb/.lib exclusion as step 4.
$strayFetch = Get-ChildItem $fetchStage -File |
    Where-Object { $_.Name -ne 'LocalScribe.Fetch.exe' -and $_.Extension -notin '.pdb', '.lib' }
if ($strayFetch) {
    Fail ("the fetch helper publish left loose files beside the exe - IncludeNativeLibrariesForSelfExtract " +
          "did not take effect: " + ($strayFetch.Name -join ', '))
}

Copy-Item (Join-Path $fetchStage 'LocalScribe.Fetch.exe') -Destination $appDir -Force

Step "8/10 bundle models"
$modelsOut = Join-Path $appDir 'models'
New-Item -ItemType Directory -Force $modelsOut | Out-Null
$modelsIn = $ModelsDir
Write-Host "  models source: $modelsIn"
# tiny + base ONLY (both f16 for CUDA and q8_0 for CPU/Vulkan, per ModelFileResolver), plus the
# VAD and the two sherpa models. large-v3-turbo and medium.en are ~4.3 GB and the assistant's two
# GGUFs are another ~2.8 GB; all six are deliberately NOT bundled - that is exactly what the in-app
# Components panel is for, and tools/fetch-models.ps1 -WriteComponentManifest pins every one of
# them so the panel can fetch them.
#
# assistant-manifest.json IS bundled even though its weights are not, and that is not an
# inconsistency: AssistantModelManifest.LoadAsync reads models/assistant-manifest.json to learn
# each model's file name, nativeCtx and pinned sha256, and without it a model the user downloads
# through the Components panel would sit on disk unusable - an empty manifest means "no models
# installed" (design 7.7, features off with an explainer).
$bundled = @(
    'silero_vad.onnx'
    'ggml-tiny.en.bin'; 'ggml-tiny.en-q8_0.bin'
    'ggml-base.en.bin'; 'ggml-base.en-q8_0.bin'
    '3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx'
    'component-manifest.json'
    'assistant-manifest.json'
)
if ($WithLargeModels) {
    $bundled += @('ggml-large-v3-turbo.bin'; 'ggml-large-v3-turbo-q5_0.bin'
                  'ggml-medium.en.bin'; 'ggml-medium.en-q5_0.bin')
}
$missing = @()
foreach ($name in $bundled) {
    $src = Join-Path $modelsIn $name
    if (Test-Path $src) { Copy-Item $src -Destination $modelsOut -Force } else { $missing += $name }
}
# The segmentation model ships as a FOLDER (tar extraction layout), not a loose file.
$seg = Join-Path $modelsIn 'sherpa-onnx-pyannote-segmentation-3-0'
if (Test-Path $seg) { Copy-Item $seg -Destination $modelsOut -Recurse -Force } else { $missing += 'sherpa-onnx-pyannote-segmentation-3-0/' }
if ($missing.Count -gt 0) {
    Fail ("models missing from $modelsIn - run tools\fetch-models.ps1 (and " +
          "tools\fetch-models.ps1 -WriteComponentManifest): " + ($missing -join ', '))
}
if ($WithLargeModels) {
    # Opt-in ONLY: this guard checks for the large weights, which the default tiny+base bundle
    # deliberately omits, so running it unconditionally would fail every normal build.
    & (Join-Path $repo 'tools\verify-import-models.ps1') -ModelsDir $modelsOut
    if ($LASTEXITCODE -ne 0) { Fail "bundled large-model guard failed" }
}

Step "8b/10 bundle ffmpeg"
# BUNDLED, not fetched (packaging design note 2026-08-06, decision 1). ffmpeg is not a user
# choice - the app cannot import audio at all without it - so there is no consent question to ask
# and no reason to make someone wait on a download. REJECTED there: fetching it too, for
# uniformity, because that converts a guaranteed-present dependency into a runtime failure mode.
#
# This step was MISSING from the plan's build.ps1 and the omission was caught by running the
# script and looking at the output (2026-08-06): the packaged app had no ffmpeg\ directory at all,
# so FfmpegLocator would have returned null on every installed machine and Import would have been
# permanently greyed out. That is precisely the shipped-to-a-stranger failure the design note was
# written to prevent - its opening paragraph describes this exact symptom seen in a worktree.
$ffmpegOut = Join-Path $appDir 'ffmpeg'
if (-not (Test-Path (Join-Path $FfmpegDir 'ffmpeg.exe'))) {
    Fail "ffmpeg tools missing from $FfmpegDir - run tools\fetch-ffmpeg.ps1 (or pass -FfmpegDir)."
}
New-Item -ItemType Directory -Force $ffmpegOut | Out-Null
# ffplay.exe is EXCLUDED: 17 MB, and nothing in the product probes for it - FfmpegLocator requires
# ffmpeg.exe and ffprobe.exe only. LICENSE.txt is kept deliberately; these are LGPL/GPL builds and
# the licence text ships with them.
Get-ChildItem $FfmpegDir -File | Where-Object { $_.Name -ne 'ffplay.exe' } |
    Copy-Item -Destination $ffmpegOut -Force
foreach ($needed in 'ffmpeg.exe', 'ffprobe.exe') {
    if (-not (Test-Path (Join-Path $ffmpegOut $needed))) { Fail "ffmpeg bundle is missing $needed" }
}
Write-Host ("  bundled {0:N1} MB of ffmpeg (ffplay.exe excluded)" -f `
    ((Get-ChildItem $ffmpegOut -File | Measure-Object Length -Sum).Sum / 1MB))

Step "9/10 refuse to package anyone's data"
# Sessions live in %USERPROFILE%\LocalScribe and settings.json in %APPDATA%\LocalScribe, so no user
# data can reach this output by the CURRENT layout. That is true by accident of layout, not by
# contract, and this installer is about to be handed to strangers - a stray recording or a
# developer's settings.json inside a signed package is not a thing to discover after the fact.
# Make it true by contract: any hit here is a build failure.
#
# %APPDATA% and %USERPROFILE% are per-user and outside $appDir, so a clean build finds nothing.
# The point is the build that is NOT clean - a hand-copied test session, a debug settings.json
# dropped in to reproduce something, a diagnostics folder left by running the app in place.
$userDataPatterns = @('settings.json', 'sessions', 'diagnostics', '*.flac', '*.jsonl')
$leaked = @()
foreach ($pattern in $userDataPatterns) {
    $leaked += Get-ChildItem -Path $appDir -Filter $pattern -Recurse -Force -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName.Substring($appDir.Length).TrimStart('\') }
}
if ($leaked.Count -gt 0) {
    Fail ("user data found in the publish output - this package must never carry anyone's " +
          "sessions, settings or diagnostics: " + ($leaked -join ', '))
}
Write-Host "  clean: no settings.json, sessions, diagnostics, .flac or .jsonl in the package."

Step "10/10 package (Velopack)"
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Fail "the Velopack CLI is not installed. Install it once with: dotnet tool install -g vpk"
}
# Read Plan A's LITERAL <Version> element. [xml] parsing does NOT evaluate MSBuild property
# functions, so a property whose value is an expression rather than a literal would come back as
# that expression's own TEXT - non-empty, therefore truthy - sail past an emptiness guard, and
# reach vpk as a package version that is not SemVer. Hence the SHAPE check below rather than a
# presence check: it rejects an unevaluated expression, a "+sha" build-metadata suffix and a typo,
# all loudly, before anything is packaged. Do NOT introduce an indirection property here; read the
# plain <Version> element and nothing else. (ShippingScriptTests asserts the name of the property
# that was tried and rejected does not appear in this file, so do not name it in a comment either -
# the same rule the zero-network grep imposes on App and Core.)
[xml] $props = Get-Content (Join-Path $repo 'src\Directory.Build.props')
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ }) -join ''
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    Fail ("src\Directory.Build.props <Version> must be a plain three-part SemVer for --packVersion; " +
          "got '$version'. InformationalVersion carries the +sha suffix - Velopack must not see it.")
}

$vpkArgs = @(
    'pack'
    '--packId', 'LocalScribe'
    '--packVersion', $version
    '--packDir', $appDir
    '--mainExe', 'LocalScribe.App.exe'
    '--packTitle', 'LocalScribe'
    '--outputDir', $relDir
    '--icon', (Join-Path $repo 'src\LocalScribe.App\Assets\LocalScribe.ico')
)
if ($CertThumbprint) {
    # signtool is shelled out to by Velopack; the timestamp URL keeps the signature valid after the
    # certificate expires, which for a product a solicitor installs once and keeps is the point.
    $vpkArgs += @('--signParams',
        "/sha1 $CertThumbprint /fd sha256 /tr http://timestamp.digicert.com /td sha256")
    Write-Host "  signing with certificate $CertThumbprint"
} else {
    Write-Host ""
    Write-Host "  ******************************************************************" -ForegroundColor Yellow
    Write-Host "  *  WARNING: building UNSIGNED.                                   *" -ForegroundColor Yellow
    Write-Host "  *  Windows SmartScreen will warn every user who runs the setup,  *" -ForegroundColor Yellow
    Write-Host "  *  and nothing proves the installer came from you.               *" -ForegroundColor Yellow
    Write-Host "  *  Supply a certificate thumbprint to sign:                      *" -ForegroundColor Yellow
    Write-Host "  *    Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert         *" -ForegroundColor Yellow
    Write-Host "  *    .\build.ps1 -CertThumbprint <40 hex>                        *" -ForegroundColor Yellow
    Write-Host "  *  or set LOCALSCRIBE_SIGN_THUMBPRINT.                           *" -ForegroundColor Yellow
    Write-Host "  ******************************************************************" -ForegroundColor Yellow
    Write-Host ""
}
& vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { Fail "Velopack packaging failed" }

Write-Host ""
Write-Host "DONE -> $relDir" -ForegroundColor Green
if (-not $CertThumbprint) { Write-Host "  (this build is UNSIGNED)" -ForegroundColor Yellow }
exit 0
