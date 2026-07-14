# tools/fetch-ffmpeg.ps1
# Fetches the SHA-pinned FFmpeg LGPL SHARED win64 build (ffmpeg.exe + ffprobe.exe + runtime DLLs
# + LICENSE.txt) into <repo>/tools/ffmpeg (gitignored). LGPL shared ONLY - never a GPL or static
# asset (Stage 7 bundles this folder beside the app with its license text; LocalScribe invokes
# ffmpeg strictly as a separate process).
#
# PIN FLOW (fail-closed, mirrors fetch-models.ps1's Assert-Sha256): the three Pinned* constants
# below start EMPTY. The FIRST run resolves the latest BtbN/FFmpeg-Builds LGPL win64 shared zip,
# downloads it, computes its SHA-256, PRINTS the tag/asset/sha to paste into the constants, and
# EXITS WITH AN ERROR - nothing is extracted from an unpinned archive. After pasting the pin,
# every run verifies the download against the pinned SHA-256 and deletes + throws on mismatch.
$ErrorActionPreference = 'Stop'

$PinnedTag    = ''   # pinned at first fetch - the unpinned run prints the value to paste here
$PinnedAsset  = ''   # pinned at first fetch - e.g. an ffmpeg-*-win64-lgpl-shared*.zip asset name
$PinnedSha256 = ''   # pinned at first fetch - SHA-256 of that exact zip

$dest = Join-Path $PSScriptRoot 'ffmpeg'
if ((Test-Path (Join-Path $dest 'ffmpeg.exe')) -and (Test-Path (Join-Path $dest 'ffprobe.exe'))) {
    Write-Host "exists: $dest (delete the folder to force a re-fetch)"
    exit 0
}

# Verifies $Path against $ExpectedSha256 (case-insensitive). Deletes the file and throws on
# mismatch - fail closed, never let a corrupt/tampered binary pass through.
function Assert-Sha256 {
    param([string] $Path, [string] $ExpectedSha256)
    $actual = (Get-FileHash -Algorithm SHA256 $Path).Hash
    Write-Host "  sha256: $actual"
    if ($actual.ToUpperInvariant() -ne $ExpectedSha256.ToUpperInvariant()) {
        Remove-Item -Force $Path
        throw "SHA256 mismatch for $Path (expected $ExpectedSha256, got $actual) - file deleted"
    }
    Write-Host "  verified: $Path"
}

if ($PinnedTag -eq '' -or $PinnedAsset -eq '' -or $PinnedSha256 -eq '') {
    Write-Host 'no pin set - resolving the latest BtbN LGPL win64 SHARED build to compute one...'
    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest'
    $asset = $release.assets |
        Where-Object { $_.name -like '*win64-lgpl-shared*.zip' -and $_.name -notlike '*gpl-shared-*gpl*' } |
        Select-Object -First 1
    if ($null -eq $asset) { throw 'no win64-lgpl-shared zip asset found on the latest BtbN release' }
    $zip = Join-Path $env:TEMP $asset.name
    Write-Host "downloading: $($asset.name) (tag $($release.tag_name))"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip
    $sha = (Get-FileHash -Algorithm SHA256 $zip).Hash
    Write-Host ''
    Write-Host 'Pin computed. Paste these three values into tools/fetch-ffmpeg.ps1 and re-run:'
    Write-Host "  `$PinnedTag    = '$($release.tag_name)'"
    Write-Host "  `$PinnedAsset  = '$($asset.name)'"
    Write-Host "  `$PinnedSha256 = '$sha'"
    throw 'unpinned run: pin the values above, then re-run (nothing was extracted)'
}

$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$PinnedTag/$PinnedAsset"
$zip = Join-Path $env:TEMP $PinnedAsset
if (-not (Test-Path $zip)) {
    Write-Host "fetching: $url"
    Invoke-WebRequest -Uri $url -OutFile $zip
}
Assert-Sha256 -Path $zip -ExpectedSha256 $PinnedSha256

$extract = Join-Path $env:TEMP ("ffmpeg-extract-" + [Guid]::NewGuid().ToString('N'))
Expand-Archive -Path $zip -DestinationPath $extract
# BtbN zip layout: <build-name>/bin/{ffmpeg.exe, ffprobe.exe, av*.dll, sw*.dll}, <build-name>/LICENSE.txt
$ffmpegExe = Get-ChildItem -Path $extract -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
if ($null -eq $ffmpegExe) { throw 'ffmpeg.exe not found inside the archive' }
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item -Path (Join-Path $ffmpegExe.DirectoryName '*') -Destination $dest -Recurse -Force
$license = Get-ChildItem -Path $extract -Recurse -Filter 'LICENSE.txt' | Select-Object -First 1
if ($null -ne $license) { Copy-Item $license.FullName -Destination $dest -Force }
Remove-Item -Recurse -Force $extract
if (-not (Test-Path (Join-Path $dest 'ffprobe.exe'))) { throw 'ffprobe.exe missing after extract' }
Write-Host "done -> $dest"
