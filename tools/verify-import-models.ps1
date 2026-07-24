# tools/verify-import-models.ps1
# Layout guard for the bundled import-time transcription models (design 2026-07-24). The
# installer must place these ggml weights in the app's models\ folder BESIDE the binary, where
# ModelPaths.ModelsRoot finds them (no code change). This list mirrors what fetch-models.ps1
# -LargeModels downloads - update both or neither.
param([Parameter(Mandatory = $true)][string] $ModelsDir)
$ErrorActionPreference = 'Stop'

$required = @(
    'ggml-large-v3-turbo.bin'
    'ggml-large-v3-turbo-q5_0.bin'
    'ggml-medium.en.bin'
    'ggml-medium.en-q5_0.bin'
)

$missing = @()
foreach ($name in $required) {
    $p = Join-Path $ModelsDir $name
    if (-not (Test-Path $p) -or (Get-Item $p).Length -eq 0) { $missing += $name }
}

if ($missing.Count -gt 0) {
    Write-Host "FAIL: bundled models at '$ModelsDir' are incomplete - missing or empty:"
    $missing | ForEach-Object { Write-Host "  $_" }
    Write-Host "Run tools/fetch-models.ps1 -LargeModels, then ensure the installer copies models\ beside the binary."
    exit 1
}
Write-Host "PASS: bundled transcription models present ($($required.Count) files)."
exit 0
