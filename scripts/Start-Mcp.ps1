$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $repo '.local/maintenance-hold.json')) { throw 'RockwellMCPServer is on operator maintenance hold. Obtain explicit human authorization before clearing the hold and reconnecting.' }
$env:STUDIO5000_PLUGIN_ROOT = $repo
$updateLockPath = Join-Path $repo '.local/application-update.lock'
if (Test-Path -LiteralPath $updateLockPath -PathType Leaf) {
    $probe = $null
    try { $probe = [IO.File]::Open($updateLockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite) }
    catch [IO.IOException] { throw 'RockwellMCPServer installation is in progress. Reconnect after the update finishes.' }
    finally { if ($probe) { $probe.Dispose() } }
}
$exe = Join-Path $repo 'artifacts/app/Mcp/Studio5000.Mcp.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Run scripts/Build.ps1 first.' }
# stdout belongs exclusively to MCP. Start the shared service separately.
& $exe
exit $LASTEXITCODE
