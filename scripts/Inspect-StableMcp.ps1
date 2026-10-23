#Requires -Version 5.1
[CmdletBinding()]
param([string]$StateRoot = (Join-Path $env:LOCALAPPDATA 'RockwellMCPServer'))
$ErrorActionPreference = 'Stop'
try {
    Import-Module (Join-Path $PSScriptRoot 'StableMcp.psm1') -Force -DisableNameChecking
    $backend = Get-RockwellStableSelection $StateRoot
    [ordered]@{ schemaVersion = 1; stateRoot = [IO.Path]::GetFullPath($StateRoot); appRoot = $backend.appRoot;
        version = $backend.manifest.version; backendId = $backend.selection.backend_id; snapshot = $backend.snapshot;
        files = @($backend.manifest.files).Count; pointerSha256 = (Get-RockwellLauncherHash (Join-Path $StateRoot 'launcher/current.json'));
        settingsSha256 = (Get-RockwellLauncherHash (Join-Path $backend.appRoot 'service.local.json'));
        backendExecuted = $false; nativeAcceptanceVerified = $false; codexConfigurationChanged = $false } | ConvertTo-Json -Depth 6
} catch { [Console]::Error.WriteLine("Rockwell stable inspection: $($_.Exception.Message)"); exit 1 }
