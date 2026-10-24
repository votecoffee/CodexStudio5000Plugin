#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AppRoot, [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$ExpectedServiceVersion,
    [string]$PredecessorStateRoot)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'StableMcp.psm1') -Force -DisableNameChecking
$AppRoot = Assert-RockwellLauncherPath $AppRoot; $Destination = Assert-RockwellLauncherPath $Destination
if (Test-Path -LiteralPath $Destination) { throw 'Pin output already exists; preserve it and prepare a fresh candidate.' }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $AppRoot 'artifacts/app/Service/Studio5000.Service.dll')).ProductVersion
if ($version -cne $ExpectedServiceVersion) { throw 'The accepted service product version differs from the reviewed version.' }
$entries = @()
foreach ($relative in @('artifacts/app/Service','artifacts/app/ExtensionWorker')) {
    $directory = Assert-RockwellLauncherPath (Join-Path $AppRoot $relative)
    foreach ($item in @(Get-ChildItem -LiteralPath $directory -Recurse -Force)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Offline runtime links are unsupported.' }
        if (!$item.PSIsContainer) {
            $entries += [ordered]@{ path=$item.FullName.Substring($AppRoot.Length+1).Replace('\','/'); size=$item.Length; sha256=(Get-RockwellLauncherHash $item.FullName) }
        }
    }
}
$script = Assert-RockwellLauncherPath (Join-Path $AppRoot 'scripts/Start-Service.ps1')
$entries += [ordered]@{ path='scripts/Start-Service.ps1'; size=([IO.FileInfo]::new($script).Length); sha256=(Get-RockwellLauncherHash $script) }
$previous = @()
if ($PredecessorStateRoot) {
    $selected = Get-RockwellStableSelection $PredecessorStateRoot
    $previous += [ordered]@{ directory=$selected.snapshot; files=@($selected.manifest.files) }
    $inherited = Join-Path $selected.snapshot 'offline-service.json'
    if (Test-Path -LiteralPath $inherited) {
        $old = Read-RockwellLauncherJson $inherited
        foreach ($entry in @($old.previousFrontends)) {
            if ($previous.Count -ge 8) { break }
            if (@($previous | Where-Object { $_.directory -ieq $entry.directory }).Count -eq 0) {
                $previous += [ordered]@{ directory=(Assert-RockwellLauncherPath $entry.directory); files=@($entry.files) }
            }
        }
    }
}
$totalBytes = [long]0
foreach ($entry in $entries) { $totalBytes += [long]$entry['size'] }
if ($entries.Count -gt 256 -or $totalBytes -gt 268435456) { throw 'Offline pin inventory exceeds its bound.' }
$value = [ordered]@{ schemaVersion=1; serviceVersion=$ExpectedServiceVersion; files=$entries; previousFrontends=$previous }
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($value | ConvertTo-Json -Depth 20 -Compress) + [Environment]::NewLine)
$stream = [IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
[ordered]@{ path=$Destination; sha256=(Get-RockwellLauncherHash $Destination); files=$entries.Count; previousFrontends=$previous.Count; serviceVersion=$ExpectedServiceVersion; backendExecuted=$false } | ConvertTo-Json
