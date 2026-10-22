param([string]$ResultsDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/test-results/idle-refresh-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
$repo = Split-Path -Parent $PSScriptRoot
$checks = [Collections.Generic.List[string]]::new()
$lock = $null
$listener = $null
function Check([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message }; $checks.Add($Message); Write-Output "PASS: $Message" }
function Reject([scriptblock]$Action, [string]$Message) { $blocked = $false; try { & $Action | Out-Null } catch { $blocked = $true }; Check $blocked $Message }
function Fake([int]$Id, [string]$Component, [datetime]$Started = [datetime]'2026-01-01') {
    $value = [pscustomobject]@{ Id = $Id; Path = (Join-Path $global:RockwellIdleFixtureRoot "artifacts/app/$Component/Studio5000.$Component.exe"); StartTime = $Started }
    $value | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($timeout) return $true }
    return $value
}
function Get-Process {
    [CmdletBinding()]param([int]$Id)
    if ($PSBoundParameters.ContainsKey('Id')) { return $global:RockwellIdleLookup[$Id] }
    return $global:RockwellIdleProcesses
}
function Stop-Process { [CmdletBinding()]param([int]$Id); $global:RockwellIdleStops.Add($Id) }
try {
    $global:RockwellIdleFixtureRoot = Join-Path $ResultsDirectory 'generated app'
    New-Item -ItemType Directory -Path (Join-Path $global:RockwellIdleFixtureRoot 'scripts'),(Join-Path $global:RockwellIdleFixtureRoot '.local') -Force | Out-Null
    foreach ($name in @('Stop-IdleMcpClients.ps1','Start-Service.ps1')) { Copy-Item -LiteralPath (Join-Path $repo "scripts/$name") -Destination (Join-Path $global:RockwellIdleFixtureRoot "scripts/$name") }
    $lockPath = Join-Path $global:RockwellIdleFixtureRoot '.local/application-update.lock'
    $lock = [IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $global:RockwellIdleProcesses = @(); $global:RockwellIdleLookup = @{}; $global:RockwellIdleStops = [Collections.Generic.List[int]]::new()
    $helper = Join-Path $global:RockwellIdleFixtureRoot 'scripts/Stop-IdleMcpClients.ps1'
    & $helper -UpdateLock $lock
    Check ($global:RockwellIdleStops.Count -eq 0) 'No running clients causes no stop request or service connection'
    $mcp = Fake 101 'Mcp'; $global:RockwellIdleProcesses = @($mcp); $global:RockwellIdleLookup[101] = $mcp
    $unrelatedLock = [IO.File]::Open((Join-Path $global:RockwellIdleFixtureRoot '.local/other.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try { Reject { & $helper -UpdateLock $unrelatedLock } 'An unrelated lock cannot authorize a frontend refresh' } finally { $unrelatedLock.Dispose() }
    foreach ($component in @('Service','Desktop','NativeBridge','CipBridge','ExtensionWorker')) {
        $global:RockwellIdleProcesses = @($mcp,(Fake 102 $component))
        Reject { & $helper -UpdateLock $lock } ("A running $component blocks refresh before any stop")
    }
    $global:RockwellIdleProcesses = @($mcp)
    Reject { & $helper -UpdateLock $lock } 'Missing service settings block refresh'
    @{ ServiceUrl = 'http://192.0.2.1:5487' } | ConvertTo-Json | Set-Content (Join-Path $global:RockwellIdleFixtureRoot 'service.local.json')
    Reject { & $helper -UpdateLock $lock } 'A nonloopback endpoint is never probed'
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $listener.Start()
    $port = $listener.LocalEndpoint.Port
    @{ ServiceUrl = "http://127.0.0.1:$port" } | ConvertTo-Json | Set-Content (Join-Path $global:RockwellIdleFixtureRoot 'service.local.json')
    Reject { & $helper -UpdateLock $lock } 'A listening service endpoint blocks refresh even without an app process match'
    Check ($global:RockwellIdleStops.Count -eq 0) 'All negative preflights preserve every process'
    $listener.Stop(); $listener = $null
    $global:RockwellIdleLookup[101] = Fake 101 'Mcp' ([datetime]'2026-02-01')
    Reject { & $helper -UpdateLock $lock } 'A recycled PID with a different creation time is not stopped'
    Check ($global:RockwellIdleStops.Count -eq 0) 'Identity mismatch leaves processes untouched'
    $global:RockwellIdleLookup[101] = $mcp
    & $helper -UpdateLock $lock
    Check ($global:RockwellIdleStops.Count -eq 1 -and $global:RockwellIdleStops[0] -eq 101) 'Only the currently identified thin frontend is stopped after connection refusal'
    Reject { & (Join-Path $global:RockwellIdleFixtureRoot 'scripts/Start-Service.ps1') -Background } 'The ordinary service launcher refuses startup while the update lock is held'
    $lock.Dispose(); $lock = $null
    @{ passed = $checks.Count; checks = $checks; nativeConnectionAttempted = $false; realProcessesStopped = $false } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $ResultsDirectory 'summary.json')
    Write-Output "$($checks.Count) idle frontend fixture checks passed: $ResultsDirectory"
} finally { if ($lock) { $lock.Dispose() }; if ($listener) { $listener.Stop() } }
