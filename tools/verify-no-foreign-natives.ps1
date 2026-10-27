# tools/verify-no-foreign-natives.ps1
# Refuses a publish that carries native binaries for a platform the installer can never run on
# (2026-10-21). LLamaSharp.Backend.Cuda12, Whisper.net.Runtime and Whisper.net.Runtime.Vulkan copy
# their natives through their own build props/targets, not as NuGet runtime assets, so
# `dotnet publish -r win-x64` never prunes them: the 0.9.2 installer carried ~570 MB of linux-x64,
# win-x86 and win-arm64 libraries that no x64 Windows process can load. src\Directory.Build.props
# now drops them from every RID-specific publish (target _DropOtherPlatformNatives); this is the
# check that the drop held, run over the fully assembled app directory.
#
# Two independent rules, because each catches what the other cannot:
#   1. a folder named for a RID other than -Rid. runtimes\win-arm64\ holds .dll files - only the
#      folder name says they are foreign;
#   2. an ELF or Mach-O library (.so, .so.N, .dylib) anywhere at all - a flattened layout would
#      put one under no RID folder.
# Pinned by tests/LocalScribe.App.Tests/ForeignNativesGuardTests.cs, which runs it in Windows
# PowerShell 5.1 - keep it 5.1-compatible.
param(
    [Parameter(Mandatory = $true)][string] $PublishDir,
    [Parameter(Mandatory = $true)][string] $Rid
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    Write-Host "FAIL: publish directory '$PublishDir' does not exist - nothing was checked."
    exit 1
}
$root = (Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd('\')

function Get-RelativePath([string] $full) {
    $full.Substring($root.Length).TrimStart('\').Replace('\', '/')
}

# Every OS prefix a .NET RID can start with, then at least one -<part> (win-x64, linux-musl-x64).
# The bare portable "win" is never a native folder in this product, so it is not matched; nor are
# the GPU/CPU variant folders (cuda, vulkan, cuda12, avx2) that sit above or below the RID folder.
$ridPattern = '^(win|linux|osx|maccatalyst|ios|iossimulator|tvos|tvossimulator|android|browser|freebsd|illumos|solaris)(-[a-z0-9]+)+$'

$foreignDirs = @()
foreach ($dir in (Get-ChildItem -LiteralPath $root -Recurse -Directory -Force | Sort-Object FullName)) {
    if ($dir.Name -notmatch $ridPattern -or $dir.Name -eq $Rid) { continue }
    # Report the outermost foreign folder only.
    $inside = $foreignDirs | Where-Object { $dir.FullName.StartsWith($_.FullName + '\', 'OrdinalIgnoreCase') }
    if (-not $inside) { $foreignDirs += $dir }
}

$foreignFiles = @()
foreach ($file in (Get-ChildItem -LiteralPath $root -Recurse -File -Force | Sort-Object FullName)) {
    if ($file.Name -notmatch '\.(so(\.\d+)*|dylib)$') { continue }
    $inside = $foreignDirs | Where-Object { $file.FullName.StartsWith($_.FullName + '\', 'OrdinalIgnoreCase') }
    if (-not $inside) { $foreignFiles += $file }
}

$hits = @()
foreach ($dir in $foreignDirs) {
    $bytes = (Get-ChildItem -LiteralPath $dir.FullName -Recurse -File -Force | Measure-Object Length -Sum).Sum
    $hits += ('{0}/  ({1:N1} MB)' -f (Get-RelativePath $dir.FullName), ($bytes / 1MB))
}
foreach ($file in $foreignFiles) {
    $hits += ('{0}  ({1:N1} MB)' -f (Get-RelativePath $file.FullName), ($file.Length / 1MB))
}

if ($hits.Count -gt 0) {
    Write-Host "FAIL: the $Rid publish at '$PublishDir' carries native binaries for another platform:"
    $hits | ForEach-Object { Write-Host "  $_" }
    Write-Host ("No $Rid process can load these. src\Directory.Build.props drops them from every " +
                "RID-specific publish (target _DropOtherPlatformNatives) - if this fired, that " +
                "target stopped matching, or a new package copies its natives some other way.")
    exit 1
}
Write-Host "PASS: no native binaries for any platform other than $Rid."
exit 0
