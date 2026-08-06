# tools/fetch-models.ps1
# Downloads the dev/fixture model files into <repo>/models (gitignored).
# Stage 7 (packaging) owns production download + SHA pinning; this is dev tooling only.
param(
    # Also fetch the LOCKED default assistant LLM (design 2026-07-18 section 7.2):
    # Qwen3-4B-Instruct-2507 q4_K_M GGUF, ~2.5 GB, Apache-2.0. SHA-pinned from the
    # Hugging Face LFS pointer (fetched over TLS before the blob), verified fail-closed,
    # and recorded into models/assistant-manifest.json (Core re-verifies on load).
    [switch] $Assistant,
    # Also fetch the large IMPORT-TIME whisper models bundled with the app (design 2026-07-24):
    # large-v3-turbo + medium.en, each f16 (CUDA) and q5_0 (CPU/Vulkan). ~4.2-4.4 GB total.
    [switch] $LargeModels,
    # Also fetch the semantic-search embedding model (design 2026-07-25):
    # EmbeddingGemma-300m Q8_0 GGUF (~300 MB, 100+ languages), served by the assistant
    # helper's "embed" op on CPU. Recorded into assistant-manifest.json with role=embedding.
    [switch] $Embedding,
    # Tier 1 plan D, T1-10 (2026-08-05): write models/component-manifest.json - the url + sha256
    # + byte size of every model the IN-APP downloader may fetch. Resolved from each file's
    # Hugging Face LFS POINTER (raw/main), which carries both "oid sha256:<hex>" and
    # "size <bytes>", so nothing has to be downloaded to produce the pins and no SHA-256 is ever
    # hand-typed into C#. Assert-Sha256 then enforces the same pin fail-closed on the app side.
    [switch] $WriteComponentManifest
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
# --- Assistant LLM (GGUF, design 2026-07-18 section 7.2; 2026-07-23: single-model) ---------
# ONLY the LOCKED default is fetched. The former optional entries (Qwen3-1.7B, Gemma-4-E2B)
# were REMOVED 2026-07-23: LlamaEngine hardcodes the ChatML non-thinking wrapper, which is
# correct for Qwen3-4B-Instruct-2507 alone - the 1.7B is a THINKING model (burns the whole
# budget in <think>, returns nothing) and Gemma is not ChatML (<start_of_turn>), both
# verified on real weights. If a second model is ever wanted: per-model template metadata in
# the manifest, selected by the engine (deferred as YAGNI).
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

# Returns BOTH values the pin manifest needs from one LFS pointer fetch.
function Get-HfPin {
    param([string] $PointerUrl)
    $resp = Invoke-WebRequest -Uri $PointerUrl
    $text = if ($resp.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($resp.Content) } else { [string]$resp.Content }
    if ($text -notmatch 'oid sha256:([0-9a-fA-F]{64})') {
        throw "no sha256 oid in LFS pointer at $PointerUrl - wrong path, or the file is not LFS-tracked"
    }
    $sha = $Matches[1].ToLowerInvariant()
    if ($text -notmatch 'size (\d+)') { throw "no size in LFS pointer at $PointerUrl" }
    return @{ Sha256 = $sha; Bytes = [long]$Matches[1] }
}

# Fetches+pins a single manifest-tracked model: resolves its SHA256 from the HF LFS pointer,
# downloads the blob if not already on disk, verifies fail-closed, and returns the manifest
# entry (including its role - "chat" or "embedding"). Shared by both the -Assistant and
# -Embedding model lists below.
function Get-PinnedModelEntry {
    param([hashtable] $m)
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
    return [ordered]@{
        canonicalName = $m.CanonicalName; file = $m.File; sha256 = $pin
        nativeCtx = $m.NativeCtx; license = $m.License; role = $m.Role
    }
}

$manifestEntries = @()

if ($Assistant) {
    # Default LOCKED: Qwen3-4B-Instruct-2507 q4_K_M (decisions log - no bake-off).
    $assistantModels = @(
        @{ CanonicalName = 'Qwen3-4B-Instruct-2507'; NativeCtx = 262144; License = 'Apache-2.0'
           Role = 'chat'
           File = 'Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
           # Qwen publishes no first-party GGUF for this model (Qwen/...-GGUF 401s = absent);
           # lmstudio-community mirrors bartowski's quant of Qwen/Qwen3-4B-Instruct-2507 under
           # the exact filename above. Provenance is still pinned+fail-closed via the LFS oid.
           Url  = 'https://huggingface.co/lmstudio-community/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
           Ptr  = 'https://huggingface.co/lmstudio-community/Qwen3-4B-Instruct-2507-GGUF/raw/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf' }
    )
    foreach ($m in $assistantModels) { $manifestEntries += Get-PinnedModelEntry $m }
}

if ($Embedding) {
    # ggml-org publishes the official llama.cpp conversion of google/embeddinggemma-300m.
    # License is Gemma (use-restricted, not OSI) - recorded verbatim in the manifest; semantic
    # search runs it locally only, which the Gemma terms permit.
    $embeddingModels = @(
        @{ CanonicalName = 'EmbeddingGemma-300m'; NativeCtx = 2048; License = 'Gemma'
           Role = 'embedding'
           File = 'embeddinggemma-300M-Q8_0.gguf'
           Url  = 'https://huggingface.co/ggml-org/embeddinggemma-300M-GGUF/resolve/main/embeddinggemma-300M-Q8_0.gguf'
           Ptr  = 'https://huggingface.co/ggml-org/embeddinggemma-300M-GGUF/raw/main/embeddinggemma-300M-Q8_0.gguf' }
    )
    foreach ($m in $embeddingModels) { $manifestEntries += Get-PinnedModelEntry $m }
}

if ($Assistant -or $Embedding) {
    if ($manifestEntries.Count -gt 0) {
        # Merge with any entries already in the manifest for files still present on disk
        # (so a plain -Assistant or -Embedding run does not drop other still-present extras).
        $manifestPath = Join-Path $models 'assistant-manifest.json'
        if (Test-Path $manifestPath) {
            $existing = (Get-Content $manifestPath -Raw | ConvertFrom-Json).models
            # Two Gemma filenames: the repo id and blob name were corrected 2026-07-23.
            # 'gemma-4-e2b-it-qat-q4_0.gguf' is what the only ever-shipped/committed version of
            # this script actually wrote (pre-fix); 'gemma-4-E2B_q4_0-it.gguf' is the corrected
            # name. -contains is an exact string match (case-insensitive only), so a manifest
            # written by either version must be listed to be dropped.
            $droppedModels = @('Qwen3-1.7B-Q4_K_M.gguf', 'gemma-4-e2b-it-qat-q4_0.gguf', 'gemma-4-E2B_q4_0-it.gguf')
            foreach ($e in $existing) {
                if ($droppedModels -contains $e.file) { continue }   # 2026-07-23: engine cannot prompt these
                if (($manifestEntries | Where-Object { $_.file -eq $e.file }).Count -eq 0 -and
                    (Test-Path (Join-Path $models $e.file))) {
                    $manifestEntries += [ordered]@{
                        canonicalName = $e.canonicalName; file = $e.file
                        sha256 = $e.sha256; nativeCtx = $e.nativeCtx; license = $e.license
                        role = $(if ($e.PSObject.Properties['role']) { $e.role } else { 'chat' })
                    }
                }
            }
        }
        $manifest = [ordered]@{ schemaVersion = 1; models = $manifestEntries }
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -Path $manifestPath -Encoding utf8
        Write-Host "manifest -> $manifestPath ($($manifestEntries.Count) model(s))"
    }
}

# --- Large import-time whisper models (design 2026-07-24) --------------------------------
# Bundled with the app so the Import dialog's model picker has high-quality choices offline.
# Both f16 (CUDA prefers it) and q5_0 (CPU/Vulkan prefer it) per model, so ModelFileResolver
# loads each backend's ideal file. SHA pinned from the HF LFS pointer (raw/main), enforced
# fail-closed. If a q5_0 filename 404s, check the ggerganov/whisper.cpp repo for the actual
# quantized name and update this list AND tools/verify-import-models.ps1 together.
if ($LargeModels) {
    # NB: must NOT be named $largeModels - PowerShell variable names are case-insensitive, so that
    # collides with the [switch] $LargeModels parameter, and assigning an array to the type-
    # constrained switch throws "Cannot convert System.Object[] to SwitchParameter" at runtime.
    $largeModelFiles = @(
        'ggml-large-v3-turbo.bin'
        'ggml-large-v3-turbo-q5_0.bin'
        'ggml-medium.en.bin'
        'ggml-medium.en-q5_0.bin'
    )
    foreach ($name in $largeModelFiles) {
        $dest = Join-Path $models $name
        $ptr  = "https://huggingface.co/ggerganov/whisper.cpp/raw/main/$name"
        $url  = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$name"
        Write-Host "pin: $name"
        $pin = Get-HfPinnedSha256 -PointerUrl $ptr
        Write-Host "  pinned sha256: $pin"
        if (-not (Test-Path $dest)) {
            Write-Host "fetching: $name"
            Get-RemoteFile -Uris @($url) -OutFile $dest
        } else {
            Write-Host "exists: $name"
        }
        Assert-Sha256 -Path $dest -ExpectedSha256 $pin   # fail-closed: deletes on mismatch
    }
}

Write-Host "done -> $models"

if ($WriteComponentManifest) {
    # Only HF-LFS-backed blobs appear here. ffmpeg, the diarizer helper and the assistant helper
    # EXECUTABLE are NOT downloadable in-app: ffmpeg comes from tools/fetch-ffmpeg.ps1 and the two
    # helpers ship in the installer, so the panel shows them as probe-only rows with a remedy
    # instead of a Download button that could not work.
    #
    # The assistant's WEIGHTS are a different matter and they ARE pinned here. build.ps1 publishes
    # the assistant helper into the installer but deliberately does NOT bundle its ~2.5 GB chat
    # model or the ~300 MB embedding model - the same reason large-v3-turbo is not bundled. Without
    # these two pins a clean install would show the assistant as present and answer nothing, with
    # no in-app route to obtain the weights at all.
    #
    # Repo is per-entry: these blobs live in three different Hugging Face repositories, so a
    # single hardcoded base URL could only ever pin whisper.
    #
    # License is carried per entry and shown in the panel BEFORE the download starts (packaging
    # design note 2026-08-06, decision 5). These are not all the same terms - the Gemma embedding
    # model in particular ships under the Gemma Terms of Use, not a plain OSS licence - and a user
    # about to put those weights on a machine that handles privileged material is entitled to know
    # that before pressing the button, not after.
    $pins = @(
        @{ Id = 'whisper-large-v3-turbo'; Name = 'Whisper large-v3-turbo'
           File = 'ggml-large-v3-turbo.bin'; Repo = 'ggerganov/whisper.cpp'; License = 'MIT' }
        @{ Id = 'whisper-large-v3-turbo-q5'; Name = 'Whisper large-v3-turbo (q5_0)'
           File = 'ggml-large-v3-turbo-q5_0.bin'; Repo = 'ggerganov/whisper.cpp'; License = 'MIT' }
        @{ Id = 'whisper-medium-en'; Name = 'Whisper medium.en'
           File = 'ggml-medium.en.bin'; Repo = 'ggerganov/whisper.cpp'; License = 'MIT' }
        @{ Id = 'whisper-medium-en-q5'; Name = 'Whisper medium.en (q5_0)'
           File = 'ggml-medium.en-q5_0.bin'; Repo = 'ggerganov/whisper.cpp'; License = 'MIT' }
        # MUST stay id 'assistant-chat' - ComponentProbe.AssistantChatPinId reads this id to decide
        # whether the assistant row is really usable, rather than naming the .gguf in C#.
        @{ Id = 'assistant-chat'; Name = 'Assistant model (Qwen3-4B-Instruct-2507 Q4_K_M)'
           File = 'Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
           Repo = 'lmstudio-community/Qwen3-4B-Instruct-2507-GGUF'; License = 'Apache-2.0' }
        @{ Id = 'assistant-embedding'; Name = 'Semantic search model (EmbeddingGemma-300m Q8_0)'
           File = 'embeddinggemma-300M-Q8_0.gguf'; Repo = 'ggml-org/embeddinggemma-300M-GGUF'
           License = 'Gemma Terms of Use' }
    )
    $entries = @()
    foreach ($p in $pins) {
        Write-Host "pin: $($p.File)"
        $pin = Get-HfPin -PointerUrl "https://huggingface.co/$($p.Repo)/raw/main/$($p.File)"
        Write-Host "  sha256 $($pin.Sha256)  bytes $($pin.Bytes)"
        $entries += [ordered]@{
            id = $p.Id; name = $p.Name; file = $p.File
            url = "https://huggingface.co/$($p.Repo)/resolve/main/$($p.File)"
            sha256 = $pin.Sha256; bytes = $pin.Bytes; license = $p.License
        }
    }
    $path = Join-Path $models 'component-manifest.json'
    [ordered]@{ schemaVersion = 1; components = $entries } |
        ConvertTo-Json -Depth 4 | Set-Content -Path $path -Encoding utf8
    Write-Host "component manifest -> $path ($($entries.Count) entries)"
}
