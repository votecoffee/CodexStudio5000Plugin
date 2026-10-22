param([Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [Parameter(Mandatory=$true)][string]$FrameworkWheel, [string]$RegistryDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$runtime = (Resolve-Path -LiteralPath (Join-Path $RuntimeDirectory 'Scripts/python.exe')).Path
$baseline = Get-Content -LiteralPath (Join-Path $repo 'framework/src/rockwell_framework/baseline.json') -Raw | ConvertFrom-Json
& $runtime -m pip check
if ($LASTEXITCODE -ne 0) { throw 'Management runtime has dependency conflicts.' }
& $runtime -m rockwell_framework.package --wheel $FrameworkWheel
if ($LASTEXITCODE -ne 0) { throw 'Management runtime provenance verification failed.' }
$stamp = Join-Path $repo '.local/management-installation.json'
if (!(Test-Path -LiteralPath $stamp)) { throw 'Run the application installer before enabling management.' }
$installation = Get-Content -LiteralPath $stamp -Raw | ConvertFrom-Json
if (!$installation.pluginId -or !$installation.appVersion) { throw 'Installed application/plugin identity is unavailable.' }
$appPackageVersion = (& $runtime -c "import importlib.metadata; print(importlib.metadata.version('rockwell-framework-host'))").Trim()
if ($LASTEXITCODE -ne 0 -or !$appPackageVersion) { throw 'App-owned management package is unavailable.' }
$pointer = [ordered]@{ schemaVersion = 1; python = $runtime; pythonSha256 = (Get-FileHash -LiteralPath $runtime).Hash;
    frameworkVersion = $baseline.version; mcpPythonSdk = $baseline.mcp_python_sdk; wheelSha256 = $baseline.wheel_sha256; appPackageVersion = $appPackageVersion }
if ($RegistryDirectory) {
    if (![IO.Path]::IsPathRooted($RegistryDirectory)) { throw 'An explicit management registry must be an absolute path.' }
    $pointer.registry = $RegistryDirectory
}
. (Join-Path $PSScriptRoot 'CodexIntegration.ps1')
Write-RockwellIntegrationJson (Join-Path $repo '.local/management-runtime.json') $pointer
Write-Output 'Selected the verified app-owned reporting supervisor for the next service start. No manager or broker was installed.'
