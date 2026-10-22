param([string]$ProjectPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $repo '.local/maintenance-hold.json')) { throw 'Operator maintenance hold blocks GUI startup until explicitly released.' }
$env:STUDIO5000_PLUGIN_ROOT = $repo
$exe = Join-Path $repo 'artifacts/app/Desktop/Studio5000.Desktop.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Run scripts/Build.ps1 first.' }
if ($ProjectPath) { & $exe --project $ProjectPath } else { & $exe }
if ($LASTEXITCODE -ne 0) { throw "Desktop application exited with code $LASTEXITCODE." }
