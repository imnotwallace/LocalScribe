# tools/verify-assistant-publish.ps1
# Layout guard for the assistant helper's folder publish (design 2026-07-23 section "Testing").
# The csproj target _PreserveLlamaCppNativeLayout is load-bearing; if it regresses, the publish
# silently reverts to a flattened noavx layout (a summary avx2 finishes in ~112s DNFs in 600s).
# The required list mirrors LocalScribe.Core.Assistant.AssistantPublishLayout.RequiredRelativePaths
# VERBATIM - AssistantPublishLayoutTests pins the two copies together. Update both or neither.
param([Parameter(Mandatory = $true)][string] $PublishDir)
$ErrorActionPreference = 'Stop'

$required = @(
    'LocalScribe.Assistant.exe'
    'runtimes/win-x64/native/avx/ggml-base.dll'
    'runtimes/win-x64/native/avx/ggml-cpu.dll'
    'runtimes/win-x64/native/avx/ggml.dll'
    'runtimes/win-x64/native/avx/llama.dll'
    'runtimes/win-x64/native/avx/mtmd.dll'
    'runtimes/win-x64/native/avx2/ggml-base.dll'
    'runtimes/win-x64/native/avx2/ggml-cpu.dll'
    'runtimes/win-x64/native/avx2/ggml.dll'
    'runtimes/win-x64/native/avx2/llama.dll'
    'runtimes/win-x64/native/avx2/mtmd.dll'
    'runtimes/win-x64/native/avx512/ggml-base.dll'
    'runtimes/win-x64/native/avx512/ggml-cpu.dll'
    'runtimes/win-x64/native/avx512/ggml.dll'
    'runtimes/win-x64/native/avx512/llama.dll'
    'runtimes/win-x64/native/avx512/mtmd.dll'
    'runtimes/win-x64/native/noavx/ggml-base.dll'
    'runtimes/win-x64/native/noavx/ggml-cpu.dll'
    'runtimes/win-x64/native/noavx/ggml.dll'
    'runtimes/win-x64/native/noavx/llama.dll'
    'runtimes/win-x64/native/noavx/mtmd.dll'
    'runtimes/win-x64/native/cuda12/ggml-base.dll'
    'runtimes/win-x64/native/cuda12/ggml-cpu.dll'
    'runtimes/win-x64/native/cuda12/ggml-cuda.dll'
    'runtimes/win-x64/native/cuda12/ggml.dll'
    'runtimes/win-x64/native/cuda12/llama.dll'
    'runtimes/win-x64/native/cuda12/mtmd.dll'
)

$missing = @()
foreach ($rel in $required) {
    $p = Join-Path $PublishDir ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path $p) -or (Get-Item $p).Length -eq 0) { $missing += $rel }
}

if ($missing.Count -gt 0) {
    Write-Host "FAIL: assistant publish at '$PublishDir' is incomplete - missing or empty:"
    $missing | ForEach-Object { Write-Host "  $_" }
    Write-Host "The publish likely regressed to a flattened layout (see LocalScribe.Assistant.csproj target _PreserveLlamaCppNativeLayout)."
    exit 1
}
Write-Host "PASS: assistant publish layout complete ($($required.Count) required files present)."
exit 0
