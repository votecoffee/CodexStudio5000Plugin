param([string]$ResultsDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if (!$ResultsDirectory) { $ResultsDirectory=Join-Path $repo ('artifacts/test-results/native-candidate-' + [guid]::NewGuid().ToString('N')) }
$ResultsDirectory=[IO.Path]::GetFullPath($ResultsDirectory)
if (Test-Path -LiteralPath $ResultsDirectory) { throw 'Use a new disposable candidate guard fixture.' }
New-Item -ItemType Directory -Path $ResultsDirectory | Out-Null
$checks=0
function New-Fixture([string]$Name) {
    $root=Join-Path $ResultsDirectory $Name
    New-Item -ItemType Directory -Path (Join-Path $root 'scripts') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-NativeLocalCandidate.ps1') -Destination (Join-Path $root 'scripts')
    [IO.File]::WriteAllText((Join-Path $root 'scripts/Start-Service.ps1'), 'param([switch]$Background) [IO.File]::WriteAllText((Join-Path (Split-Path -Parent $PSScriptRoot) ''.local/started''), $env:ROCKWELL_RESOURCE_REGISTRY)')
    New-Item -ItemType Directory -Path (Join-Path $root '.local') | Out-Null
    $options=@{ NativeExecutionProfile='local-project'; EnableNativeGuiLaunch=$false; EnableControllerOperations=$false; EnableSdkServiceRecovery=$false; AutoStartSdkService=$false;
        AllowAnyControllerPath=$false; AllowAnySourcePath=$false; AllowAnyOutputPath=$false; ControllerTargets=@(); CipBridgePath=$null }
    [IO.File]::WriteAllText((Join-Path $root 'service.local.json'), (@{ Studio5000=$options } | ConvertTo-Json -Depth 5))
    $files=[ordered]@{}
    foreach ($path in Get-ChildItem -LiteralPath $root -File -Recurse) { $files[$path.FullName.Substring($root.Length+1).Replace('\','/')]=(Get-FileHash -LiteralPath $path.FullName).Hash }
    $manifest=@{ schemaVersion=1; state='prepared'; profile='local-project'; nativeAcceptance='pending'; files=$files }
    [IO.File]::WriteAllText((Join-Path $root 'candidate.json'), ($manifest | ConvertTo-Json -Depth 5))
    return $root
}
function Invoke-Fixture([string]$Root, [bool]$Refused) {
    $hash=(Get-FileHash -LiteralPath (Join-Path $Root 'candidate.json')).Hash
    $failed=$false
    try { & (Join-Path $Root 'scripts/Start-NativeLocalCandidate.ps1') -ExpectedManifestSha256 $hash -Background }
    catch { $failed=$true; if (!$Refused) { throw } }
    if ($failed -ne $Refused -or ((Test-Path -LiteralPath (Join-Path $Root '.local/started')) -eq $Refused)) { throw 'Candidate guard result differs from expectation.' }
    $script:checks++
}
$previous=[Environment]::GetEnvironmentVariable('ROCKWELL_RESOURCE_REGISTRY')
try {
    $env:ROCKWELL_RESOURCE_REGISTRY='unrelated-registry-fixture'
    $root=New-Fixture 'valid'; Invoke-Fixture $root $false
    if ((Get-Content -LiteralPath (Join-Path $root '.local/started') -Raw) -cne (Join-Path $root '.local/resource-registry') -or $env:ROCKWELL_RESOURCE_REGISTRY -cne 'unrelated-registry-fixture') { throw 'Dedicated registry or caller environment preservation failed.' }; $checks++
    $root=New-Fixture 'changed'; Add-Content -LiteralPath (Join-Path $root 'scripts/Start-Service.ps1') '# changed'; Invoke-Fixture $root $true
    $root=New-Fixture 'extra'; [IO.File]::WriteAllText((Join-Path $root 'scripts/unreviewed.dll'), 'not executed'); Invoke-Fixture $root $true
    $root=New-Fixture 'shared-host'; [IO.File]::WriteAllText((Join-Path $root '.local/management-runtime.json'), '{}'); Invoke-Fixture $root $true
    $root=New-Fixture 'widened'; $config=Get-Content -LiteralPath (Join-Path $root 'service.local.json') -Raw | ConvertFrom-Json; $config.Studio5000.EnableControllerOperations=$true
    [IO.File]::WriteAllText((Join-Path $root 'service.local.json'), ($config | ConvertTo-Json -Depth 5))
    # Even with a newly reviewed content hash, the typed profile boundary refuses widening.
    $manifest=Get-Content -LiteralPath (Join-Path $root 'candidate.json') -Raw | ConvertFrom-Json; $manifest.files.'service.local.json'=(Get-FileHash -LiteralPath (Join-Path $root 'service.local.json')).Hash
    [IO.File]::WriteAllText((Join-Path $root 'candidate.json'), ($manifest | ConvertTo-Json -Depth 5)); Invoke-Fixture $root $true
    Write-Output "$checks harmless candidate launch guards passed; no service/vendor executable was launched: $ResultsDirectory"
} finally { [Environment]::SetEnvironmentVariable('ROCKWELL_RESOURCE_REGISTRY',$previous) }
