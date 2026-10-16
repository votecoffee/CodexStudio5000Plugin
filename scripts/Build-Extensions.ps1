param([string]$PackageRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'extensions'), [string]$Version = '0.8.0')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$published = Join-Path $repo ('artifacts/extension-builds/' + [guid]::NewGuid().ToString('N'))
dotnet publish (Join-Path $repo 'src/Studio5000.Extensions.Rockwell/Studio5000.Extensions.Rockwell.csproj') -c Release -o $published --nologo
if ($LASTEXITCODE -ne 0) { throw 'Extension publish failed.' }
foreach ($module in @(@('logix', 'LogixExtension'), @('factorytalk', 'FactoryTalkExtension'), @('viewdesigner', 'ViewDesignerExtension'), @('watches', 'WatchesExtension'))) {
    $existing = Join-Path $PackageRoot "$($module[0])/versions/$Version/manifest.json"
    if (Test-Path -LiteralPath $existing) {
        $manifest = Get-Content -LiteralPath $existing -Raw | ConvertFrom-Json
        $same = @($manifest.files.PSObject.Properties).Count -eq @(Get-ChildItem -LiteralPath $published -File -Recurse).Count
        foreach ($file in Get-ChildItem -LiteralPath $published -File -Recurse) {
            $relative = $file.FullName.Substring($published.TrimEnd('\').Length + 1).Replace('\', '/')
            if ($manifest.files.$relative -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) { $same = $false; break }
        }
        if (!$same) { throw "Package $($module[0]) $Version has different bytes. Supply a new -Version." }
        Write-Output "Reusing immutable package $($module[0]) $Version"
    } else {
        & (Join-Path $PSScriptRoot 'Publish-Extension.ps1') -Id $module[0] -Version $Version -PublishedDirectory $published -EntryAssembly 'Studio5000.Extensions.Rockwell.dll' -EntryType "Studio5000.Extensions.Rockwell.$($module[1])" -PackageRoot $PackageRoot
    }
}
