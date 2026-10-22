param([string]$ResultsDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/test-results/installer-settings-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
$repo = Split-Path -Parent $PSScriptRoot
$checks = [Collections.Generic.List[string]]::new()
function Check([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message }; $checks.Add($Message); Write-Output "PASS: $Message" }
foreach ($case in @('absolute','relative','new-bindings')) {
    $fixture = Join-Path $ResultsDirectory $case
    New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts') -Force | Out-Null
    foreach ($name in @('Install.ps1','Assert-ApplicationsStopped.ps1')) { Copy-Item -LiteralPath (Join-Path $repo "scripts/$name") -Destination (Join-Path $fixture "scripts/$name") }
    foreach ($name in @('Service','Mcp','Desktop','ExtensionWorker')) {
        $directory = Join-Path $fixture "artifacts/app/$name"
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $directory "Studio5000.$name.exe"), 'Generated marker; never executed.')
    }
    @'
param([string]$Route)
Write-Output "Generated fixture integration: $Route"
'@ | Set-Content (Join-Path $fixture 'scripts/Configure-Codex.ps1')
    @'
param([switch]$EnableSdk, [switch]$EnableCip, [int]$Port)
@{ sdk = [bool]$EnableSdk; cip = [bool]$EnableCip; port = $Port } | ConvertTo-Json | Set-Content (Join-Path (Split-Path -Parent $PSScriptRoot) '.local/initialize-call.json')
'@ | Set-Content (Join-Path $fixture 'scripts/Initialize.ps1')
    $native = Join-Path $fixture 'artifacts/app/NativeBridge/Studio5000.NativeBridge.exe'
    $cip = Join-Path $fixture 'artifacts/app/CipBridge/Studio5000.CipBridge.exe'
    if ($case -eq 'relative') { $native = 'artifacts/app/NativeBridge/Studio5000.NativeBridge.exe'; $cip = $null }
    if ($case -eq 'new-bindings') { $native = $null; $cip = $null }
    $configPath = Join-Path $fixture 'service.local.json'
    @{ ServiceUrl = 'http://127.0.0.1:44321'; Studio5000 = @{ NativeBridgePath = $native; CipBridgePath = $cip; EnableControllerOperations = $true; AllowAnyControllerPath = $true; MaximumConcurrentMutations = 7 } } | ConvertTo-Json -Depth 5 | Set-Content $configPath
    $before = (Get-FileHash -LiteralPath $configPath).Hash
    Push-Location -LiteralPath $ResultsDirectory
    try { & (Join-Path $fixture 'scripts/Install.ps1') -SkipBuild -WithSdk -WithCip:($case -ne 'relative') -NoShortcut -CodexIntegration None }
    finally { Pop-Location }
    if ($case -eq 'new-bindings') {
        $call = Get-Content (Join-Path $fixture '.local/initialize-call.json') -Raw | ConvertFrom-Json
        Check ($call.sdk -and $call.cip -and $call.port -eq 44321) 'Explicit new bridge selections retain the configured service port'
    } else {
        Check (!(Test-Path (Join-Path $fixture '.local/initialize-call.json'))) ("Existing $case bridge selections do not invoke initialization")
        Check ((Get-FileHash -LiteralPath $configPath).Hash -eq $before) ("Existing $case service settings preserve exact bytes and broad policy")
    }
}
@{ passed = $checks.Count; checks = $checks; nativeConnectionAttempted = $false } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $ResultsDirectory 'summary.json')
Write-Output "$($checks.Count) installer settings fixture checks passed: $ResultsDirectory"
