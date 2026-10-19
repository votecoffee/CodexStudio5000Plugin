param([ValidateRange(37, 99)][int]$Revision = 38)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$run = Join-Path $repo ('artifacts/version-validation/' + [guid]::NewGuid().ToString('N'))
$previousRoot = $env:STUDIO5000_PLUGIN_ROOT
Push-Location -LiteralPath $repo
try {
    $env:STUDIO5000_PLUGIN_ROOT = $repo
    # Publish a separate bridge/verifier; the installed service and its settings are not touched.
    dotnet publish src/Studio5000.NativeBridge/Studio5000.NativeBridge.csproj -c Release -o (Join-Path $run 'bridge') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Native bridge build failed; install the licensed SDK local package.' }
    dotnet publish tests/Studio5000.Integration/Studio5000.Integration.csproj -c Release -o (Join-Path $run 'verifier') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Version verifier build failed.' }
    & (Join-Path $run 'verifier/Studio5000.Integration.exe') '--sdk-version-smoke' $Revision (Join-Path $run 'bridge/Studio5000.NativeBridge.exe')
    if ($LASTEXITCODE -ne 0) { throw "Offline Logix v$Revision verification failed. Retain the reported evidence; do not retry native work automatically." }
} finally { $env:STUDIO5000_PLUGIN_ROOT = $previousRoot; Pop-Location }
