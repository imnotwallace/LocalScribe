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
#
# Tier 1 plan D, T1-10 (2026-08-05): this list was three names and two of them were wrong against
# a real shipped layout, so build.ps1 could never have used this guard as a gate.
#   - LocalScribe.Diarizer.exe was REMOVED. It is not a collision, it is the REQUIRED layout:
#     CompositionRoot resolves the helper at Path.Combine(AppContext.BaseDirectory,
#     "LocalScribe.Diarizer.exe"). The single-file publish carries its natives INSIDE the exe
#     (-p:IncludeNativeLibrariesForSelfExtract=true), so the exe beside the app is safe and the
#     loose DLLs are the actual hazard.
#   - onnxruntime.dll was REMOVED. Measured 2026-08-05: a RID-specific publish FLATTENS
#     runtimes/<rid>/native/ into the output root and emits no runtimes/ folder at all, and
#     LocalScribe.App references Microsoft.ML.OnnxRuntime 1.22.0, so App's OWN onnxruntime.dll
#     legitimately sits beside the app in every published build. A name-based absence check cannot
#     tell App's 1.22 from sherpa's 1.24.4 and would fail every build on the correct file.
# What is left is the one name that is unambiguously sherpa's and that App can never produce. It is
# a COMPLETE discriminator, not a weakening: the same measured publish emitted onnxruntime.dll,
# sherpa-onnx.dll and sherpa-onnx-c-api.dll together, so the whole-folder dev copy this guard was
# written for still trips it. REJECTED: sniffing onnxruntime.dll's FileVersion for "1.22.x" - it
# adds a second thing to maintain on every ORT bump for no extra scenario caught.
$forbiddenBesideApp = @(
    'sherpa-onnx-c-api.dll'
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
    Write-Host "The helper's loose native payload must never be flattened into the app directory."
    Write-Host "The single-file LocalScribe.Diarizer.exe itself SHOULD be there - CompositionRoot"
    Write-Host "resolves it at AppContext.BaseDirectory - and is deliberately not checked here."
    exit 1
}

Write-Host "PASS: diarizer helper present ($($required.Count) required file) and no sherpa payload beside the app."
exit 0
