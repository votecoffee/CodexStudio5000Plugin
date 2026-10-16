param([string]$ProjectPath)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Start-Service.ps1') -Background
if ($ProjectPath) { & (Join-Path $PSScriptRoot 'Start-Gui.ps1') -ProjectPath $ProjectPath }
else { & (Join-Path $PSScriptRoot 'Start-Gui.ps1') }
