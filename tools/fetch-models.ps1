# tools/fetch-models.ps1
# Downloads the dev/fixture model files into <repo>/models (gitignored).
# Stage 7 (packaging) owns production download + SHA pinning; this is dev tooling only.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$models = Join-Path $root 'models'
New-Item -ItemType Directory -Force $models | Out-Null

$files = @(
    @{ Name = 'silero_vad.onnx'
       Url  = 'https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx' },
    @{ Name = 'ggml-tiny.en.bin'
       Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin' },
    @{ Name = 'ggml-base.en.bin'
       Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin' }
)

foreach ($f in $files) {
    $dest = Join-Path $models $f.Name
    if (Test-Path $dest) { Write-Host "exists: $($f.Name)"; continue }
    Write-Host "fetching: $($f.Name)"
    Invoke-WebRequest -Uri $f.Url -OutFile $dest
    $sha = (Get-FileHash $dest -Algorithm SHA256).Hash
    Write-Host "  sha256: $sha"
}
Write-Host "done -> $models"
