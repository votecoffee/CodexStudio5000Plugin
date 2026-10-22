param([string]$ResultsDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/test-results/installer-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$ResultsDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ResultsDirectory)
$fixture = Join-Path $ResultsDirectory 'isolated checkout with spaces'
$fixtureScripts = Join-Path $fixture 'scripts'
$packages = Join-Path $fixture 'extensions'
New-Item -ItemType Directory -Path $fixtureScripts -Force | Out-Null
foreach ($name in @('Build-Extensions.ps1', 'Publish-Extension.ps1', 'Assert-ApplicationsStopped.ps1', 'ReleaseVersion.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $fixtureScripts
}
$builder = Join-Path $fixtureScripts 'Build-Extensions.ps1'
$checks = [Collections.Generic.List[string]]::new()
$ids = @('logix', 'factorytalk', 'viewdesigner', 'watches')
$fixturePayload = 'generated fixture A'

# Exercise the real packaging scripts with generated file payloads. No compiled
# worker is executed, host binaries replaced or controller connection attempted.
function dotnet {
    $outputIndex = [Array]::IndexOf([string[]]$args, '-o')
    if ($args[0] -ne 'publish' -or $outputIndex -lt 0) { throw 'Unexpected fixture publish command.' }
    $publishTarget = $args[$outputIndex + 1]
    New-Item -ItemType Directory -Path $publishTarget -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $publishTarget 'Studio5000.Extensions.Rockwell.dll'), $fixturePayload)
    [IO.File]::WriteAllText((Join-Path $publishTarget 'Studio5000.Extensions.Rockwell.pdb'), $fixturePayload + ' revision metadata')
    $global:LASTEXITCODE = 0
}
function Check([bool]$condition, [string]$description) {
    if (!$condition) { throw $description }
    $checks.Add($description)
    Write-Output "PASS: $description"
}
function Selected([string]$id) { (Get-Content -LiteralPath (Join-Path $packages "$id/active.json") -Raw | ConvertFrom-Json).version }
function AllSelected([string]$version) { @($ids | Where-Object { (Selected $_) -cne $version }).Count -eq 0 }
function Snapshot([string]$root, [string]$version) {
    $entries = foreach ($id in $ids) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root "$id/versions/$version") -File -Recurse | Sort-Object FullName) {
            $id + ':' + $file.Name + ':' + (Get-FileHash -LiteralPath $file.FullName).Hash
        }
    }
    $entries -join "`n"
}

& $builder -PackageRoot $packages -Version fixture -ContentVersion -SelectOnStartup
$first = Selected 'logix'
Check ($first -cmatch '^fixture-build\.[a-f0-9]{20}$') 'A new build receives a content-qualified version'
Check (AllSelected $first) 'All four bundled modules select the installed build'
$firstSnapshot = Snapshot $packages $first
& $builder -PackageRoot $packages -Version fixture -ContentVersion -SelectOnStartup
Check ((AllSelected $first) -and (Snapshot $packages $first) -ceq $firstSnapshot) 'An identical rebuild reuses the immutable version without changing its files'

$fixturePayload = 'generated fixture B'
& $builder -PackageRoot $packages -Version fixture -ContentVersion
$second = @(Get-ChildItem -LiteralPath (Join-Path $packages 'logix/versions') -Directory | Where-Object { $_.Name -cne $first })
Check ($second.Count -eq 1 -and $second[0].Name -cmatch '^fixture-build\.[a-f0-9]{20}$') 'Changed binary/revision metadata creates a distinct version'
$secondVersion = $second[0].Name
Check (AllSelected $first) 'Extension-only packaging preserves existing startup selections'
Check ((Snapshot $packages $first) -ceq $firstSnapshot) 'Publishing changed bytes preserves the previous package for rollback'

$badManifest = Join-Path $packages "watches/versions/$secondVersion/manifest.json"
$originalManifest = [IO.File]::ReadAllBytes($badManifest)
$altered = Get-Content -LiteralPath $badManifest -Raw | ConvertFrom-Json
$altered.entryType = 'Generated.InvalidEntry'
$altered | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $badManifest -Encoding utf8
$rejected = $false
try { & $builder -PackageRoot $packages -Version fixture -ContentVersion -SelectOnStartup }
catch { $rejected = $_.Exception.Message -like '*different bytes*' }
Check $rejected 'A mismatched existing manifest is rejected instead of overwritten'
Check (AllSelected $first) 'A failed fourth package leaves all previous startup selections intact'
[IO.File]::WriteAllBytes($badManifest, $originalManifest)
& $builder -PackageRoot $packages -Version fixture -ContentVersion -SelectOnStartup
Check (AllSelected $secondVersion) 'A complete stopped-host install selects all newly built packages'
Check ((Snapshot $packages $first) -ceq $firstSnapshot) 'Selecting the new build retains exact rollback package bytes'

$explicitPackages = Join-Path $fixture 'explicit release versions'
& $builder -PackageRoot $explicitPackages -Version fixed
$fixedSnapshot = Snapshot $explicitPackages fixed
$fixturePayload = 'generated fixture C'
$rejected = $false
try { & $builder -PackageRoot $explicitPackages -Version fixed }
catch { $rejected = $_.Exception.Message -like '*different bytes*' }
Check $rejected 'Explicit release versions still reject changed package content'
Check ((Snapshot $explicitPackages fixed) -ceq $fixedSnapshot) 'A rejected explicit rebuild preserves every previously installed file'

[pscustomobject]@{ checks = $checks; count = $checks.Count; fixturePayloadsOnly = $true; workerStarted = $false; controllerConnectionAttempted = $false } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.json') -Encoding utf8
Write-Output "$($checks.Count) installer package checks passed: $ResultsDirectory"
