param([string]$ProjectPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$env:STUDIO5000_PLUGIN_ROOT = $repo
$exe = Join-Path $repo 'artifacts/app/Desktop/Studio5000.Desktop.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Run scripts/Build.ps1 first.' }
if ($ProjectPath) { & $exe --project $ProjectPath } else { & $exe }
