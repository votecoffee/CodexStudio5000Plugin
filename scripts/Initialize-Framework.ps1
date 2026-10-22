param([Parameter(Mandatory=$true)][string]$FrameworkWheel,
    [string]$RuntimeDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) '.local/framework-runtime'),
    [string]$Python = 'py')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$baseline = Get-Content -LiteralPath (Join-Path $repo 'framework/src/rockwell_framework/baseline.json') -Raw | ConvertFrom-Json
$FrameworkWheel = (Resolve-Path -LiteralPath $FrameworkWheel).Path
if ([IO.Path]::GetFileName($FrameworkWheel) -ne $baseline.wheel -or
    (Get-FileHash -LiteralPath $FrameworkWheel -Algorithm SHA256).Hash.ToLowerInvariant() -ne $baseline.wheel_sha256) {
    throw 'Framework wheel differs from the reviewed baseline. Update the dependency pin explicitly.'
}
$RuntimeDirectory = [IO.Path]::GetFullPath($RuntimeDirectory)
if (Test-Path -LiteralPath $RuntimeDirectory) { throw 'Use a new runtime directory. Never replace an environment used by a running host.' }
& $Python -m venv $RuntimeDirectory
if ($LASTEXITCODE -ne 0) { throw 'Python virtual environment creation failed.' }
$runtime = Join-Path $RuntimeDirectory 'Scripts/python.exe'
& $runtime -m pip install --no-deps $FrameworkWheel
if ($LASTEXITCODE -ne 0) { throw 'Verified framework installation failed.' }
& $runtime -m pip install -r (Join-Path $repo 'framework/requirements-runtime-lock.txt')
if ($LASTEXITCODE -ne 0) { throw 'Locked framework dependencies failed.' }
& $runtime -m pip install --no-deps (Join-Path $repo 'framework')
if ($LASTEXITCODE -ne 0) { throw 'Rockwell framework package installation failed.' }
& $runtime -m pip check
if ($LASTEXITCODE -ne 0) { throw 'Framework environment has dependency conflicts.' }
& $runtime -m rockwell_framework.package --wheel $FrameworkWheel
if ($LASTEXITCODE -ne 0) { throw 'Installed framework bytes differ from the reviewed wheel.' }
Write-Output "Prepared isolated framework runtime: $RuntimeDirectory"
Write-Output 'No host, plugin installation, registration or controller connection was started.'
