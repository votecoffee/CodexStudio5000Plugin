$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $PSScriptRoot
$runtime = Get-Content -LiteralPath (Join-Path $package 'runtime.json') -Raw | ConvertFrom-Json
if ($runtime.schemaVersion -ne 1 -or ![IO.Path]::IsPathRooted($runtime.root)) { throw 'Invalid RockwellMCPServer runtime pointer; rerun the app installer.' }
$launcher = Join-Path $runtime.root 'scripts/Start-Mcp.ps1'
if (!(Test-Path -LiteralPath $launcher -PathType Leaf)) { throw 'RockwellMCPServer moved or is unavailable; rerun its installer.' }
# The app owns all execution/state. stdout belongs exclusively to MCP.
& $launcher
exit $LASTEXITCODE
