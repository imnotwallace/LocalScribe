# tools/smoke-embed.ps1
# Real-weights smoke for the assistant helper's "embed" op (design 2026-07-25).
# Requires: models/<embedding gguf> fetched (fetch-models.ps1 -Embedding) and the helper built.
# Pipes one embed request into the helper and asserts on the embedResult line.
param(
    [string] $HelperExe = "src/LocalScribe.Assistant/bin/Debug/net10.0-windows/LocalScribe.Assistant.exe",
    [string] $ModelFile = "models/embeddinggemma-300M-Q8_0.gguf",
    [int]    $Dim = 256
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $HelperExe)) { throw "helper not built: $HelperExe (dotnet build src/LocalScribe.Assistant)" }
if (-not (Test-Path $ModelFile)) { throw "model missing: $ModelFile (tools/fetch-models.ps1 -Embedding)" }
$model = (Resolve-Path $ModelFile).Path -replace '\\', '\\'
$payload = '{"kind":"document","dim":' + $Dim + ',"texts":["we could settle at three hundred and fifty thousand","the weather is nice today"]}'
$request = '{"op":"embed","modelPath":"' + $model + '","ctxTokens":2048,"backend":"cpu","keepAlive":false,"payload":' + $payload + '}'
$lines = $request | & $HelperExe 2>$null
$result = $lines | Where-Object { $_ -match '"type":"embedResult"' } | Select-Object -First 1
if (-not $result) { throw "no embedResult line. Helper output:`n$($lines -join "`n")" }
$obj = $result | ConvertFrom-Json
if ($obj.embeddings.Count -ne 2) { throw "expected 2 vectors, got $($obj.embeddings.Count)" }
if ($obj.embeddings[0].Count -ne $Dim) { throw "expected dim $Dim, got $($obj.embeddings[0].Count)" }
for ($i = 0; $i -lt $obj.embeddings.Count; $i++) {
    $norm = [Math]::Sqrt(($obj.embeddings[$i] | ForEach-Object { $_ * $_ } | Measure-Object -Sum).Sum)
    if ([Math]::Abs($norm - 1.0) -gt 0.01) { throw "vector $i not unit-normalized (norm=$norm)" }
}
Write-Host "method: $($obj.method)"
Write-Host "PASS: 2 vectors, dim $Dim, unit-normalized"
