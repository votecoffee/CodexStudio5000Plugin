#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory=Join-Path $repo 'artifacts/test-tools/frontdoor-prep' }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if (!$OutputDirectory.StartsWith(([IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))+'\'),[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Prepare disposable fixture payloads only under this checkout artifacts directory.'
}
Push-Location -LiteralPath $repo
try {
    if (@(git status --porcelain).Count) { throw 'Commit the reviewed source first so fixture provenance is accurate.' }
    $commit=(git rev-parse HEAD).Trim()
    $cohort=$OutputDirectory+'-cohort.json'
    if ((Test-Path -LiteralPath $OutputDirectory) -or (Test-Path -LiteralPath $cohort)) {
        throw 'Fixture output exists; preserve evidence and use a fresh output directory.'
    }
    dotnet build tests/Studio5000.ResourceInterop/Studio5000.ResourceInterop.csproj -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Harmless fixture helper build failed.' }
    foreach ($name in @('Mcp','StdioHost')) {
        dotnet publish "src/Studio5000.$name/Studio5000.$name.csproj" -c Release --no-restore -o $OutputDirectory --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Fixture frontend publish failed; retain partial bytes.' }
    }
    $release=Get-Content -LiteralPath (Join-Path $repo 'release-version.json') -Raw | ConvertFrom-Json
    $files=@(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=$_.FullName.Substring($OutputDirectory.Length+1).Replace('\','/');size=$_.Length;
            sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $value=[ordered]@{version=$release.displayVersion;commit=$commit;sourceTreeClean=$true;files=$files;
        scope='Isolated fixture payload only; no live service, selected backend or controller changed.'}
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($value | ConvertTo-Json -Depth 10)+([char]10))
    $stream=[IO.File]::Open($cohort,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length);$stream.Flush($true) } finally { $stream.Dispose() }
    Write-Output "Prepared $OutputDirectory; use ROCKWELL_FRONTDOOR_PAYLOAD for its isolated tests."
} finally { Pop-Location }
