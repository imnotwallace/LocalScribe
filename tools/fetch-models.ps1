# tools/fetch-models.ps1
# Downloads the dev/fixture model files into <repo>/models (gitignored).
# Stage 7 (packaging) owns production download + SHA pinning; this is dev tooling only.
param(
    # Also fetch the LOCKED default assistant LLM (design 2026-07-18 section 7.2):
    # Qwen3-4B-Instruct-2507 q4_K_M GGUF, ~2.5 GB, Apache-2.0. SHA-pinned from the
    # Hugging Face LFS pointer (fetched over TLS before the blob), verified fail-closed,
    # and recorded into models/assistant-manifest.json (Core re-verifies on load).
    [switch] $Assistant,
    # Also fetch the optional assistant entries (Qwen3-1.7B q4_K_M ~1 GB, Gemma 4 E2B QAT).
    [switch] $AssistantOptional
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$models = Join-Path $root 'models'
New-Item -ItemType Directory -Force $models | Out-Null

# Downloads $OutFile from the first working URL in $Uris, retrying each URL a few
# times with backoff. If a prior attempt left a partial file on disk, later attempts
# resume it (-Resume) instead of starting over - this matters on boxes where large
# GitHub release assets get throttled or the connection drops mid-download.
function Get-RemoteFile {
    param(
        [string[]] $Uris,
        [string]   $OutFile,
        [int]      $MaxAttempts = 4
    )
    $lastError = $null
    foreach ($uri in $Uris) {
        for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
            try {
                if (Test-Path $OutFile) {
                    # Partial (or already-complete) file from a prior attempt - resume it.
                    # If the file on disk is already fully downloaded, the server answers
                    # the resume range request with 416 and Invoke-WebRequest returns that
                    # response object instead of throwing (this is the resume-is-done
                    # signal, not a failure) - discard it so it doesn't spill to the console.
                    $null = Invoke-WebRequest -Uri $uri -OutFile $OutFile -Resume
                } else {
                    Invoke-WebRequest -Uri $uri -OutFile $OutFile
                }
                return
            } catch {
                $lastError = $_
                Write-Host "  attempt $attempt from $uri failed: $($_.Exception.Message)"
                if ($attempt -lt $MaxAttempts) {
                    Start-Sleep -Seconds ([Math]::Min(30, [Math]::Pow(2, $attempt)))
                }
            }
        }
        Write-Host "  giving up on $uri after $MaxAttempts attempts; trying next mirror if any"
    }
    throw "failed to download $OutFile from all mirrors: $lastError"
}

# Verifies $Path against $ExpectedSha256 (case-insensitive). Deletes the file and
# throws on mismatch - fail closed, never let a corrupt/tampered model pass through.
function Assert-Sha256 {
    param(
        [string] $Path,
        [string] $ExpectedSha256
    )
    $actual = (Get-FileHash -Algorithm SHA256 $Path).Hash
    Write-Host "  sha256: $actual"
    if ($actual.ToUpperInvariant() -ne $ExpectedSha256.ToUpperInvariant()) {
        Remove-Item -Force $Path
        throw "SHA256 mismatch for $Path (expected $ExpectedSha256, got $actual) - file deleted"
    }
    Write-Host "  verified: $Path"
}

$files = @(
    @{ Name = 'silero_vad.onnx'
       Url  = 'https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx' },
    @{ Name = 'ggml-tiny.en.bin'
       Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin' },
    @{ Name = 'ggml-base.en.bin'
       Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin' },
    @{ Name = 'ggml-small.en.bin'
       Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin' },

    # q8_0 quantized whisper weights: preferred on CPU/Vulkan (ModelFileResolver) - near-lossless
    # accuracy at ~half the f16 memory traffic. CUDA keeps the plain f16 files above (spec 3).
    @{ Name = 'ggml-tiny.en-q8_0.bin'
       Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en-q8_0.bin' },
    @{ Name = 'ggml-base.en-q8_0.bin'
       Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en-q8_0.bin' },
    @{ Name = 'ggml-small.en-q8_0.bin'
       Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en-q8_0.bin' },

    # --- Stage 5 diarisation models (Apache-2.0 / MIT only, SHA-pinned) ---

    # Embedding: 3D-Speaker CAM++ zh+en common (Apache-2.0, non-VoxCeleb). HF mirror
    # is tried first - byte-identical to the GitHub release asset, but this box gets
    # throttled by GitHub on large release downloads; GitHub kept as a fallback.
    # NOTE the upstream typo "speaker-recongition-models" in the GitHub release tag -
    # do not "fix" it, it is the real path.
    @{ Name = '3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx'
       Urls = @(
           'https://huggingface.co/csukuangfj/speaker-embedding-models/resolve/main/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx',
           'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx'
       )
       Sha256 = 'aa3cfc16963a10586a9393f5035d6d6b57e98d358b347f80c2a30bf4f00ceba2' },

    # Segmentation: pyannote segmentation-3.0 (MIT), shipped as a .tar.bz2. Extracted
    # layout is models/sherpa-onnx-pyannote-segmentation-3-0/{model.onnx, LICENSE, ...}.
    # The MIT LICENSE inside the tarball is preserved on disk (never deleted) - Stage 6
    # packaging is expected to fold it into the app's third-party notices.
    # The release ships no vendor checksum; Sha256 below is self-computed on the
    # extracted model.onnx (not the tarball, which has no stable/pinnable content hash
    # published upstream).
    @{ Name             = 'sherpa-onnx-pyannote-segmentation-3-0.tar.bz2'
       Url              = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2'
       Archive          = 'tar.bz2'
       ExtractedRelPath = 'sherpa-onnx-pyannote-segmentation-3-0/model.onnx'
       Sha256           = '220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079' }
)

foreach ($f in $files) {
    if ($f.ContainsKey('Archive')) {
        # Archive entries (currently just the segmentation tarball): the pin is on the
        # extracted file, not the tarball, so presence/verification key off that path.
        $extracted = Join-Path $models $f.ExtractedRelPath
        if (Test-Path $extracted) {
            Write-Host "exists: $($f.ExtractedRelPath)"
        } else {
            $tarDest = Join-Path $models $f.Name
            Write-Host "fetching: $($f.Name)"
            Get-RemoteFile -Uris @($f.Url) -OutFile $tarDest
            Write-Host "extracting: $($f.Name)"
            # --force-local: GNU tar (Git for Windows ships this, not bsdtar) otherwise
            # treats a drive-letter path like "F:\..." as HOST:FILE remote-archive syntax
            # and tries to rsh/ssh to a host named "F" instead of reading the local file.
            tar --force-local -xjf $tarDest -C $models
            if ($LASTEXITCODE -ne 0) { throw "tar extraction failed for $($f.Name) (exit $LASTEXITCODE)" }
            Remove-Item -Force $tarDest -ErrorAction SilentlyContinue
        }
        # Always re-verify, even on the already-extracted path - fail closed.
        Assert-Sha256 -Path $extracted -ExpectedSha256 $f.Sha256
        continue
    }

    $dest = Join-Path $models $f.Name
    if (Test-Path $dest) {
        Write-Host "exists: $($f.Name)"
        if ($f.ContainsKey('Sha256')) { Assert-Sha256 -Path $dest -ExpectedSha256 $f.Sha256 }
        continue
    }

    Write-Host "fetching: $($f.Name)"
    $uris = if ($f.ContainsKey('Urls')) { $f.Urls } else { @($f.Url) }
    Get-RemoteFile -Uris $uris -OutFile $dest

    if ($f.ContainsKey('Sha256')) {
        Assert-Sha256 -Path $dest -ExpectedSha256 $f.Sha256
    } else {
        $sha = (Get-FileHash $dest -Algorithm SHA256).Hash
        Write-Host "  sha256: $sha"
    }
}
# --- Assistant LLMs (GGUF, design 2026-07-18 section 7.2) -------------------------------
# The sha256 pin comes from the Hugging Face LFS pointer file (raw/main), fetched over TLS
# BEFORE the multi-GB blob; Assert-Sha256 then enforces it fail-closed, and the verified
# pin lands in models/assistant-manifest.json, which the app re-verifies on every load.
function Get-HfPinnedSha256 {
    param([string] $PointerUrl)
    $resp = Invoke-WebRequest -Uri $PointerUrl
    $text = if ($resp.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($resp.Content) } else { [string]$resp.Content }
    if ($text -match 'oid sha256:([0-9a-fA-F]{64})') { return $Matches[1].ToLowerInvariant() }
    throw "no sha256 oid in LFS pointer at $PointerUrl - wrong path, or the file is not LFS-tracked"
}

if ($Assistant -or $AssistantOptional) {
    # Default LOCKED: Qwen3-4B-Instruct-2507 q4_K_M (decisions log - no bake-off).
    # Optional: Qwen3-1.7B q4_K_M (low-end/CPU-only), Gemma 4 E2B QAT (Gemma ToU).
    # NOTE (plan deviation 2): confirm the optional repos' exact paths on Hugging Face at
    # execution time - Get-HfPinnedSha256 fails loudly on a wrong path, nothing silent.
    $assistantModels = @(
        @{ CanonicalName = 'Qwen3-4B-Instruct-2507'; NativeCtx = 262144; License = 'Apache-2.0'
           File = 'Qwen3-4B-Instruct-2507-Q4_K_M.gguf'; Optional = $false
           Url  = 'https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
           Ptr  = 'https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507-GGUF/raw/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf' },
        @{ CanonicalName = 'Qwen3-1.7B-Instruct'; NativeCtx = 32768; License = 'Apache-2.0'
           File = 'Qwen3-1.7B-Q4_K_M.gguf'; Optional = $true
           Url  = 'https://huggingface.co/Qwen/Qwen3-1.7B-GGUF/resolve/main/Qwen3-1.7B-Q4_K_M.gguf'
           Ptr  = 'https://huggingface.co/Qwen/Qwen3-1.7B-GGUF/raw/main/Qwen3-1.7B-Q4_K_M.gguf' },
        @{ CanonicalName = 'Gemma-4-E2B-QAT'; NativeCtx = 32768; License = 'Gemma Terms of Use'
           File = 'gemma-4-e2b-it-qat-q4_0.gguf'; Optional = $true
           Url  = 'https://huggingface.co/google/gemma-4-e2b-it-qat-q4_0-gguf/resolve/main/gemma-4-e2b-it-qat-q4_0.gguf'
           Ptr  = 'https://huggingface.co/google/gemma-4-e2b-it-qat-q4_0-gguf/raw/main/gemma-4-e2b-it-qat-q4_0.gguf' }
    )

    $manifestEntries = @()
    foreach ($m in $assistantModels) {
        if ($m.Optional -and -not $AssistantOptional) { continue }
        $dest = Join-Path $models $m.File
        Write-Host "pin: $($m.File)"
        $pin = Get-HfPinnedSha256 -PointerUrl $m.Ptr
        Write-Host "  pinned sha256: $pin"
        if (-not (Test-Path $dest)) {
            Write-Host "fetching: $($m.File)"
            Get-RemoteFile -Uris @($m.Url) -OutFile $dest
        } else {
            Write-Host "exists: $($m.File)"
        }
        Assert-Sha256 -Path $dest -ExpectedSha256 $pin   # fail-closed: deletes on mismatch
        $manifestEntries += [ordered]@{
            canonicalName = $m.CanonicalName
            file          = $m.File
            sha256        = $pin
            nativeCtx     = $m.NativeCtx
            license       = $m.License
        }
    }

    if ($manifestEntries.Count -gt 0) {
        # Merge with any entries already in the manifest for files still present on disk
        # (so -Assistant after -AssistantOptional does not drop the optional entries).
        $manifestPath = Join-Path $models 'assistant-manifest.json'
        if (Test-Path $manifestPath) {
            $existing = (Get-Content $manifestPath -Raw | ConvertFrom-Json).models
            foreach ($e in $existing) {
                if (($manifestEntries | Where-Object { $_.file -eq $e.file }).Count -eq 0 -and
                    (Test-Path (Join-Path $models $e.file))) {
                    $manifestEntries += [ordered]@{
                        canonicalName = $e.canonicalName; file = $e.file
                        sha256 = $e.sha256; nativeCtx = $e.nativeCtx; license = $e.license
                    }
                }
            }
        }
        $manifest = [ordered]@{ schemaVersion = 1; models = $manifestEntries }
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -Path $manifestPath -Encoding utf8
        Write-Host "manifest -> $manifestPath ($($manifestEntries.Count) model(s))"
    }
}

Write-Host "done -> $models"
