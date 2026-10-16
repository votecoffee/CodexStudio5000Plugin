param(
    [Parameter(Mandatory)][string]$Id,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PublishedDirectory,
    [Parameter(Mandatory)][string]$EntryAssembly,
    [Parameter(Mandatory)][string]$EntryType,
    [string]$PackageRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'extensions')
)
$ErrorActionPreference = 'Stop'
foreach ($name in @($Id, $Version)) { if ($name -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,95}$') { throw 'Invalid extension ID/version.' } }
$source = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$destination = Join-Path $PackageRoot "$Id/versions/$Version"
if (Test-Path -LiteralPath $destination) { throw 'Version directory already exists. Publish a new version; never overwrite an installed package.' }
$files = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object FullName) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package links are not accepted.' }
    $relative = $file.FullName.Substring($source.TrimEnd('\').Length + 1).Replace('\', '/')
    if ($relative -eq 'manifest.json') { throw 'Use a clean dotnet publish directory, without a manifest.' }
    $files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
if (!$files.Contains($EntryAssembly)) { throw 'Entry assembly is missing from the publish directory.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($relative in $files.Keys) {
    $target = Join-Path $destination $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source $relative) -Destination $target
}
$json = @{ id = $Id; version = $Version; contractVersion = 1; entryAssembly = $EntryAssembly; entryType = $EntryType; files = $files } | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText((Join-Path $destination 'manifest.json'), $json, [Text.UTF8Encoding]::new($false))
$pointer = Join-Path $PackageRoot "$Id/active.json"
if (!(Test-Path -LiteralPath $pointer)) { [IO.File]::WriteAllText($pointer, (@{ version = $Version } | ConvertTo-Json), [Text.UTF8Encoding]::new($false)) }
Write-Output "Installed $Id $Version. Use studio_extensions_reload with this ID/version to activate it in a running host."
