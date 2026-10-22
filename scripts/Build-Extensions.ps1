param([string]$PackageRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'extensions'), [string]$Version = '0.36.4', [ValidateRange(1,4)][int]$ParallelWorkers = 4, [switch]$ContentVersion, [switch]$SelectOnStartup)
$ErrorActionPreference = 'Stop'
$PackageRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PackageRoot)
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,95}$') { throw 'Invalid extension version.' }
if ($SelectOnStartup) { & (Join-Path $PSScriptRoot 'Assert-ApplicationsStopped.ps1') }
$repo = Split-Path -Parent $PSScriptRoot
$published = Join-Path $repo ('artifacts/extension-builds/' + [guid]::NewGuid().ToString('N'))
dotnet publish (Join-Path $repo 'src/Studio5000.Extensions.Rockwell/Studio5000.Extensions.Rockwell.csproj') -c Release -o $published --nologo
if ($LASTEXITCODE -ne 0) { throw 'Extension publish failed.' }
$modules = @(@('logix', 'LogixExtension'), @('factorytalk', 'FactoryTalkExtension'), @('viewdesigner', 'ViewDesignerExtension'), @('watches', 'WatchesExtension'))
$files = [ordered]@{}
[string[]]$paths = @(Get-ChildItem -LiteralPath $published -File -Recurse | ForEach-Object { $_.FullName })
[Array]::Sort($paths, [StringComparer]::Ordinal)
foreach ($path in $paths) {
    $relative = $path.Substring($published.TrimEnd('\').Length + 1).Replace('\', '/')
    $files[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
if ($ContentVersion) {
    $identity = @('contract=1', 'assembly=Studio5000.Extensions.Rockwell.dll', ('parallelWorkers=' + $ParallelWorkers)) + @($modules | ForEach-Object { "module=$($_[0]):$($_[1])" }) + @($files.Keys | ForEach-Object { $_ + ':' + $files[$_] })
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes(($identity -join "`n")))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
    $Version += '-build.' + $digest.Substring(0, 20)
    if ($Version.Length -gt 96) { throw 'Content-qualified extension version exceeds 96 characters; use a shorter base version.' }
}
foreach ($module in $modules) {
    $existing = Join-Path $PackageRoot "$($module[0])/versions/$Version/manifest.json"
    if (Test-Path -LiteralPath $existing) {
        $manifest = Get-Content -LiteralPath $existing -Raw | ConvertFrom-Json
        $same = $manifest.id -ceq $module[0] -and $manifest.version -ceq $Version -and $manifest.contractVersion -eq 1 -and $manifest.parallelWorkers -eq $ParallelWorkers -and
            $manifest.entryAssembly -ceq 'Studio5000.Extensions.Rockwell.dll' -and $manifest.entryType -ceq "Studio5000.Extensions.Rockwell.$($module[1])" -and
            @($manifest.files.PSObject.Properties).Count -eq $files.Count
        foreach ($relative in $files.Keys) {
            if ($manifest.files.$relative -cne $files[$relative]) { $same = $false; break }
        }
        if (!$same) { throw "Package $($module[0]) $Version has different bytes. Supply a new -Version." }
        Write-Output "Reusing immutable package $($module[0]) $Version"
    } else {
        & (Join-Path $PSScriptRoot 'Publish-Extension.ps1') -Id $module[0] -Version $Version -ParallelWorkers $ParallelWorkers -PublishedDirectory $published -EntryAssembly 'Studio5000.Extensions.Rockwell.dll' -EntryType "Studio5000.Extensions.Rockwell.$($module[1])" -PackageRoot $PackageRoot
    }
}
if ($SelectOnStartup) {
    & (Join-Path $PSScriptRoot 'Assert-ApplicationsStopped.ps1')
    foreach ($module in $modules) {
        $pointer = Join-Path $PackageRoot "$($module[0])/active.json"
        $temporary = $pointer + '.' + [guid]::NewGuid().ToString('N')
        try {
            [IO.File]::WriteAllText($temporary, (@{ version = $Version } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
            for ($attempt = 0; ; $attempt++) {
                try {
                    if (Test-Path -LiteralPath $pointer) { [IO.File]::Replace($temporary, $pointer, [NullString]::Value) }
                    else { [IO.File]::Move($temporary, $pointer) }
                    break
                } catch [IO.IOException] {
                    # Windows indexing/antivirus can briefly hold the replaced
                    # file. Retry the atomic rename only while its source exists.
                    if ($attempt -ge 4 -or !(Test-Path -LiteralPath $temporary)) { throw }
                    Start-Sleep -Milliseconds (50 * [Math]::Pow(2, $attempt))
                }
            }
        } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
        Write-Output "Selected $($module[0]) $Version for the next service startup."
    }
}
