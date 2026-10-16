$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$env:STUDIO5000_PLUGIN_ROOT = $repo
$exe = Join-Path $repo 'artifacts/app/Mcp/Studio5000.Mcp.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Run scripts/Build.ps1 first.' }
# stdout belongs exclusively to MCP. Start the shared service separately.
& $exe
exit $LASTEXITCODE
