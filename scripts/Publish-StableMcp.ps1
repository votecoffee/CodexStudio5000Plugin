#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceDirectory, [Parameter(Mandatory)][string]$CandidateManifest,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedManifestSha256,
    [Parameter(Mandatory)][string]$AppRoot,
    [Parameter(Mandatory)][ValidatePattern('^(absent|[a-f0-9]{64})$')][string]$ExpectedPointerSha256,
    [string]$StateRoot = (Join-Path $env:LOCALAPPDATA 'RockwellMCPServer'), [switch]$PlanOnly)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'StableMcp.psm1') -Force -DisableNameChecking
function Write-LauncherBytes([string]$Path, [byte[]]$Bytes, [string]$Backup) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    # One publication only. A lost/failed replace retains temporary bytes for
    # inspection; neither backend execution nor another publication is replayed.
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, $Backup) }
    else { [IO.File]::Move($temporary, $Path) }
}
try {
    $SourceDirectory = Assert-RockwellLauncherPath $SourceDirectory; $CandidateManifest = Assert-RockwellLauncherPath $CandidateManifest
    $AppRoot = Assert-RockwellLauncherPath $AppRoot; $StateRoot = Assert-RockwellLauncherPath $StateRoot
    if ($StateRoot -match '(?i)[\\/]plugins[\\/]cache(?:[\\/]|$)' -or
        $StateRoot.StartsWith($SourceDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $SourceDirectory.StartsWith($StateRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $StateRoot -ieq $SourceDirectory) { throw 'App-owned state must be outside Codex caches and the source payload.' }
    if (!(Test-Path -LiteralPath (Join-Path $AppRoot 'service.local.json') -PathType Leaf)) { throw 'Select the existing application root and its preserved settings.' }
    if ([IO.FileInfo]::new($CandidateManifest).Length -gt 524288) { throw 'Candidate manifest exceeds its budget.' }
    if ((Get-RockwellLauncherHash $CandidateManifest) -cne $ExpectedManifestSha256) { throw 'Candidate manifest changed after review.' }
    $source = Read-RockwellLauncherJson $CandidateManifest 524288
    if ($source.sourceTreeClean -isnot [bool] -or !$source.sourceTreeClean -or $source.sourceCommit -notmatch '^[a-f0-9]{40}$' -or $source.kind -cne 'MCP frontend closure only' -or $source.state -cne 'staged-not-selected') { throw 'Use a clean, reviewed, staged MCP frontend candidate.' }
    $entries = @($source.payloads | ForEach-Object {
        if (!$_.path.StartsWith('Mcp/', [StringComparison]::Ordinal) -or $_.path.Length -le 4) { throw 'Candidate must contain only the complete MCP closure.' }
        [pscustomobject][ordered]@{ path = $_.path.Substring(4); sha256 = $_.sha256; size = [long]$_.size }
    })
    $paths = [string[]]@($entries | ForEach-Object { $_.path }); [Array]::Sort($paths, [StringComparer]::Ordinal)
    $ordered = @($paths | ForEach-Object { $name = $_; $matches = @($entries | Where-Object { $_.path -ceq $name }); if ($matches.Count -ne 1) { throw 'Duplicate source member.' }; $matches[0] })
    $manifest = [pscustomobject][ordered]@{ schema_version = 1; contract = 'rockwell-owned-stdio-v1'; version = $source.version;
        source_manifest_sha256 = $ExpectedManifestSha256; source_commit = $source.sourceCommit; files = $ordered }
    $backendId = Assert-RockwellBackend $SourceDirectory $manifest -Unpublished
    $core = @($ordered | Where-Object { $_.path -ceq 'Studio5000.Core.dll' })
    if ($core.Count -ne 1 -or $core[0].sha256 -cne $source.coreSha256) { throw 'Reviewed Core cohort differs.' }
    $pointer = Join-Path $StateRoot 'launcher/current.json'
    $currentHash = if (Test-Path -LiteralPath $pointer) { Get-RockwellLauncherHash $pointer } else { 'absent' }
    if ($currentHash -cne $ExpectedPointerSha256) { throw 'Stable backend selection changed; review again.' }
    $encoding = [Text.UTF8Encoding]::new($false); $manifestBytes = $encoding.GetBytes(($manifest | ConvertTo-Json -Depth 12 -Compress) + "`n")
    $snapshot = Get-RockwellBackendDirectory $StateRoot $backendId
    if ((Join-Path $snapshot 'Studio5000.StdioHost.exe').Length -gt 258) { throw 'Choose a shorter app-owned state root for Windows executable startup.' }
    $entrypoint = Join-Path $StateRoot 'Start-Mcp.ps1'
    $proposal = [ordered]@{ command = (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'); args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$entrypoint); cwd = $StateRoot }
    $plan = [ordered]@{ schemaVersion = 1; appRoot = $AppRoot; stateRoot = $StateRoot; backendId = $backendId; snapshot = $snapshot;
        reviewedCandidateSha256 = $ExpectedManifestSha256; expectedPointerSha256 = $ExpectedPointerSha256; entrypoint = $entrypoint;
        proposedBinding = $proposal; codexConfigChanged = $false; backendExecuted = $false; settingsChanged = $false; selected = $false }
    if ($PlanOnly) { $plan | ConvertTo-Json -Depth 10; exit 0 }
    if (!(Test-Path -LiteralPath $StateRoot)) { New-Item -ItemType Directory -Path $StateRoot > $null }
    $marker = Join-Path $StateRoot '.rockwell-launcher.json'
    if (!(Test-Path -LiteralPath $marker) -and @(Get-ChildItem -LiteralPath $StateRoot -Force).Count) { throw 'Refusing to claim a nonempty unrecognized app-owned directory.' }
    if (Test-Path -LiteralPath $marker) {
        $identity = Read-RockwellLauncherJson $marker 1024
        if ($identity.schema_version -ne 1 -or $identity.app_id -cne 'rockwell-mcp-server') { throw 'Foreign app-owned directory.' }
    }
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = [Security.AccessControl.DirectorySecurity]::new(); $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($user, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    $directory = [IO.DirectoryInfo]::new($StateRoot)
    if ($PSVersionTable.PSEdition -eq 'Desktop') { $directory.SetAccessControl($acl) }
    else { [IO.FileSystemAclExtensions]::SetAccessControl($directory, $acl) }
    $control = Join-Path $StateRoot 'launcher'; New-Item -ItemType Directory -Path $control -Force > $null
    $lock = [IO.File]::Open((Join-Path $control 'publication.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $currentHash = if (Test-Path -LiteralPath $pointer) { Get-RockwellLauncherHash $pointer } else { 'absent' }
        if ($currentHash -cne $ExpectedPointerSha256) { throw 'Stable selection changed while publication was waiting.' }
        if (Test-Path -LiteralPath $pointer) { $null = Get-RockwellStableSelection $StateRoot }
        if (!(Test-Path -LiteralPath $marker)) { Write-LauncherBytes $marker ($encoding.GetBytes('{"schema_version":1,"app_id":"rockwell-mcp-server"}')) $null }
        $backends = Join-Path $control 'backends'; New-Item -ItemType Directory -Path $backends -Force > $null
        if (!(Test-Path -LiteralPath $snapshot)) {
            $staging = Join-Path $backends ('.stage-' + [Guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $staging > $null
            foreach ($file in $ordered) {
                $destination = Join-Path $staging $file.path; New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force > $null
                Copy-Item -LiteralPath (Join-Path $SourceDirectory $file.path) -Destination $destination
            }
            $null = Assert-RockwellBackend $staging $manifest -Unpublished
            [IO.File]::WriteAllBytes((Join-Path $staging 'backend.json'), $manifestBytes)
            foreach ($path in @($staging,$snapshot)) {
                if (!(Assert-RockwellLauncherPath $path).StartsWith((Assert-RockwellLauncherPath $backends) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Snapshot move escaped the app-owned backend directory.' }
            }
            Move-Item -LiteralPath $staging -Destination $snapshot
        }
        $existing = Read-RockwellLauncherJson (Join-Path $snapshot 'backend.json')
        if ((Assert-RockwellBackend $snapshot $existing) -cne $backendId) { throw 'Existing immutable backend differs.' }
        $history = Join-Path $control 'history'; New-Item -ItemType Directory -Path $history -Force > $null
        foreach ($pair in @(@('Start-StableMcp.ps1','Start-Mcp.ps1'), @('StableMcp.psm1','StableMcp.psm1'))) {
            $destination = Join-Path $StateRoot $pair[1]; $bytes = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot $pair[0]))
            if (!(Test-Path -LiteralPath $destination) -or (Get-RockwellLauncherHash $destination) -cne (Get-RockwellLauncherHash (Join-Path $PSScriptRoot $pair[0]))) {
                $backup = if (Test-Path -LiteralPath $destination) { Join-Path $history ($pair[1] + '-' + (Get-RockwellLauncherHash $destination)) } else { $null }
                Write-LauncherBytes $destination $bytes $backup
            }
        }
        $selection = [pscustomobject][ordered]@{ schema_version = 1; contract = 'rockwell-owned-stdio-v1'; app_root = $AppRoot; backend_id = $backendId;
            backend_manifest_sha256 = (Get-RockwellLauncherHash (Join-Path $snapshot 'backend.json')); launcher_sha256 = (Get-RockwellLauncherHash $entrypoint);
            module_sha256 = (Get-RockwellLauncherHash (Join-Path $StateRoot 'StableMcp.psm1')); selection_sha256 = '' }
        $selection.selection_sha256 = Get-RockwellSelectionIdentity $selection
        $selectionBytes = $encoding.GetBytes(($selection | ConvertTo-Json -Depth 8 -Compress) + "`n")
        $backup = if ($currentHash -ne 'absent') { Join-Path $history ('selection-' + $currentHash + '.json') } else { $null }
        if (!(Test-Path -LiteralPath $pointer) -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($pointer)) -cne [Convert]::ToBase64String($selectionBytes)) { Write-LauncherBytes $pointer $selectionBytes $backup }
        $null = Get-RockwellStableSelection $StateRoot
        $plan.selected = $true; $plan['pointerSha256'] = Get-RockwellLauncherHash $pointer
        $plan | ConvertTo-Json -Depth 10
    } finally { if ($lock) { $lock.Dispose() } }
} catch { [Console]::Error.WriteLine("Rockwell stable publication: $($_.Exception.Message)"); exit 1 }
