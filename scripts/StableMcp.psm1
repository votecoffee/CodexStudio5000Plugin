# Shared, side-effect-free metadata checks for the app-owned stdio launcher.
Set-StrictMode -Version Latest
function Get-RockwellLauncherHash([string]$Path) {
    $hash = [Security.Cryptography.SHA256]::Create(); $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $hash.Dispose() }
}
function Get-RockwellLauncherTextHash([string]$Text) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
}
function Assert-RockwellLauncherPath([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:[\\/]') { throw 'An absolute local Windows path is required.' }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.TrimEnd('\') -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw 'A launcher/application path must be below the drive root.' }
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Launcher paths must not contain links or junctions.' }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if (!$parent -or $parent -eq $cursor) { break }; $cursor = $parent
    }
    return $full.TrimEnd('\')
}
function Read-RockwellLauncherJson([string]$Path, [long]$Maximum = 262144) {
    $null = Assert-RockwellLauncherPath $Path
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.PSIsContainer -or $file.Length -gt $Maximum) { throw 'Launcher metadata is missing or exceeds its budget.' }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($Path)).TrimStart([char]0xfeff)
    return $text | ConvertFrom-Json
}
function Get-RockwellBackendIdentity([object]$Manifest) {
    $lines = @('contract=rockwell-owned-stdio-v1', ('version=' + $Manifest.version), ('source=' + $Manifest.source_manifest_sha256), ('commit=' + $Manifest.source_commit))
    foreach ($file in $Manifest.files) { $lines += $file.path + ':' + $file.sha256 + ':' + ([long]$file.size).ToString([Globalization.CultureInfo]::InvariantCulture) }
    return Get-RockwellLauncherTextHash ([string]::Join("`n", $lines))
}
function Get-RockwellSelectionIdentity([object]$Selection) {
    return Get-RockwellLauncherTextHash ([string]::Join("`n", @('contract=rockwell-owned-stdio-v1', ('root=' + $Selection.app_root), ('backend=' + $Selection.backend_id), ('manifest=' + $Selection.backend_manifest_sha256), ('launcher=' + $Selection.launcher_sha256), ('module=' + $Selection.module_sha256))))
}
function Get-RockwellBackendDirectory([string]$StateRoot, [string]$Identity) {
    if ($Identity -notmatch '^[a-f0-9]{64}$') { throw 'Invalid backend identity.' }
    # Keep Windows executable paths short; the complete 256-bit identity is
    # still verified against the manifest. A prefix collision is a refusal.
    return Join-Path $StateRoot ('launcher/backends/' + $Identity.Substring(0,32))
}
function Assert-RockwellBackend([string]$Directory, [object]$Manifest, [switch]$Unpublished) {
    $Directory = Assert-RockwellLauncherPath $Directory
    if ($Manifest.schema_version -ne 1 -or $Manifest.contract -cne 'rockwell-owned-stdio-v1' -or
        $Manifest.version -notmatch '^\d{2}\.\d{1,4}\.\d{1,6}$' -or $Manifest.source_manifest_sha256 -notmatch '^[a-f0-9]{64}$' -or $Manifest.source_commit -notmatch '^[a-f0-9]{40}$') { throw 'Invalid immutable backend contract.' }
    $files = @($Manifest.files)
    if ($files.Count -lt 9 -or $files.Count -gt 256) { throw 'Invalid backend file budget.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase); $total = 0L; $previous = $null
    foreach ($file in $files) {
        $relative = [string]$file.path
        if (!$relative -or $relative.Length -gt 512 -or $relative.Contains('\') -or $file.sha256 -notmatch '^[a-f0-9]{64}$' -or [long]$file.size -lt 0) { throw 'Invalid backend member.' }
        foreach ($part in $relative.Split('/')) {
            if (!$part -or $part -in @('.','..') -or $part.TrimEnd(' ','.') -cne $part -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Noncanonical backend member.' }
        }
        if ($relative -ceq 'backend.json' -or !$seen.Add($relative) -or ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $relative) -ge 0)) { throw 'Duplicate, reserved or unsorted backend member.' }
        $previous = $relative; $path = Assert-RockwellLauncherPath (Join-Path $Directory $relative)
        if (!$path.StartsWith($Directory + '\', [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Backend member missing or outside snapshot.' }
        $size = (Get-Item -LiteralPath $path).Length; $total += $size
        if ($total -gt 268435456 -or $size -ne [long]$file.size -or (Get-RockwellLauncherHash $path) -cne $file.sha256) { throw 'Backend byte inventory differs.' }
    }
    foreach ($required in @('Studio5000.StdioHost.exe','Studio5000.StdioHost.dll','Studio5000.StdioHost.deps.json','Studio5000.StdioHost.runtimeconfig.json','Studio5000.Mcp.exe','Studio5000.Mcp.dll','Studio5000.Mcp.deps.json','Studio5000.Mcp.runtimeconfig.json','Studio5000.Core.dll')) {
        if (!$seen.Contains($required)) { throw 'The complete owned stdio/backend closure is required.' }
    }
    $items = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Snapshot links are unsupported.' }
    $actual = @($items | Where-Object { !$_.PSIsContainer -and ($Unpublished -or $_.FullName -ine (Join-Path $Directory 'backend.json')) })
    if ($actual.Count -ne $files.Count) { throw 'Snapshot has missing or extra files.' }
    foreach ($file in $actual) { if (!$seen.Contains($file.FullName.Substring($Directory.Length + 1).Replace('\','/'))) { throw 'Snapshot inventory differs.' } }
    return Get-RockwellBackendIdentity $Manifest
}
function Get-RockwellStableSelection([string]$StateRoot) {
    $StateRoot = Assert-RockwellLauncherPath $StateRoot
    $selection = Read-RockwellLauncherJson (Join-Path $StateRoot 'launcher/current.json') 65536
    if ($selection.schema_version -ne 1 -or $selection.contract -cne 'rockwell-owned-stdio-v1' -or $selection.backend_id -notmatch '^[a-f0-9]{64}$' -or $selection.backend_manifest_sha256 -notmatch '^[a-f0-9]{64}$' -or $selection.launcher_sha256 -notmatch '^[a-f0-9]{64}$' -or $selection.module_sha256 -notmatch '^[a-f0-9]{64}$' -or $selection.selection_sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid backend selection.' }
    if ((Get-RockwellSelectionIdentity $selection) -cne $selection.selection_sha256) { throw 'Backend selection checksum differs.' }
    $root = Assert-RockwellLauncherPath $selection.app_root
    if (!(Test-Path -LiteralPath (Join-Path $root 'service.local.json') -PathType Leaf)) { throw 'Selected application configuration is missing.' }
    if ((Get-RockwellLauncherHash (Join-Path $StateRoot 'Start-Mcp.ps1')) -cne $selection.launcher_sha256 -or (Get-RockwellLauncherHash (Join-Path $StateRoot 'StableMcp.psm1')) -cne $selection.module_sha256) { throw 'Stable launcher/module bytes differ.' }
    $snapshot = Get-RockwellBackendDirectory $StateRoot $selection.backend_id
    $manifestPath = Join-Path $snapshot 'backend.json'
    $null = Assert-RockwellLauncherPath $manifestPath
    if ([IO.FileInfo]::new($manifestPath).Length -gt 262144) { throw 'Backend manifest exceeds its budget.' }
    if ((Get-RockwellLauncherHash $manifestPath) -cne $selection.backend_manifest_sha256) { throw 'Backend manifest bytes differ.' }
    $manifest = Read-RockwellLauncherJson $manifestPath
    if ((Assert-RockwellBackend $snapshot $manifest) -cne $selection.backend_id) { throw 'Backend content identity differs.' }
    return [pscustomobject]@{ selection = $selection; appRoot = $root; snapshot = $snapshot; manifest = $manifest }
}
Export-ModuleMember -Function *-Rockwell*
