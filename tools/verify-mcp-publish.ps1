# tools/verify-mcp-publish.ps1
# Layout guard for the MCP server's publish directory.
# The MCP server is pure managed code with no native payload, so the required list is minimal.
param([Parameter(Mandatory = $true)][string] $PublishDir)
$ErrorActionPreference = 'Stop'

$required = @(
    'LocalScribe.Mcp.exe'
)

$missing = @()
foreach ($rel in $required) {
    $p = Join-Path $PublishDir ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path $p) -or (Get-Item $p).Length -eq 0) { $missing += $rel }
}

if ($missing.Count -gt 0) {
    Write-Host "FAIL: MCP publish at '$PublishDir' is incomplete - missing or empty:"
    $missing | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "PASS: MCP publish layout complete ($($required.Count) required files present)."
exit 0
