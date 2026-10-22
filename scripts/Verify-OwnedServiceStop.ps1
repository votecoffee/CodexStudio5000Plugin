# File and process-object stubs only. No server, real PID lookup or process stop.
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('rockwell-owned-stop-' + [Guid]::NewGuid().ToString('N'))
$scriptPath = Join-Path $root 'scripts/Stop-Service.ps1'
$local = Join-Path $root '.local'
$registry = Join-Path $root 'registry'
$resourcePath = Join-Path $registry ('resources/' + ('b' * 32) + '.json')
$checks = 0
New-Item -ItemType Directory -Path (Split-Path $scriptPath),$local,(Split-Path $resourcePath) -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Stop-Service.ps1') -Destination $scriptPath
$global:RockwellOwnedStopFixture = @{ Calls = 0; Disposals = 0; Waits = 0; Root = $root; ResourcePath = $resourcePath }
function Get-Process { [CmdletBinding()]param([int]$Id); $global:RockwellOwnedStopFixture.Calls++; return $global:RockwellOwnedStopFixture.Process }
function Stop-Process { throw 'A ledger-owned service must never reach PID termination.' }
function Get-CimInstance { throw 'A ledger-owned service must never use a parent-PID guess.' }
try {
    $started = [DateTime]::UtcNow.AddMinutes(-1)
    $image = Join-Path $root 'artifacts/app/Service/Studio5000.Service.exe'
    $process = [pscustomobject]@{ Id = 424242; Handle = 1; Path = $image; StartTime = $started; HasExited = $false }
    $process | Add-Member ScriptMethod Dispose { $global:RockwellOwnedStopFixture.Disposals++ }
    $process | Add-Member ScriptMethod WaitForExit {
        param($milliseconds)
        $global:RockwellOwnedStopFixture.Waits++
        $pending = Get-Content -LiteralPath (Join-Path $global:RockwellOwnedStopFixture.Root '.local/service-stop-request.json') -Raw | ConvertFrom-Json
        if ($pending.requestId -cne ('c' * 32) -or $pending.signature -cne '3a28490c7a446fe3a0a8d0319b2abbd9cf4e4fae9d9354bede5ee0b31682678a') { throw 'Stop request failed the independent Python HMAC fixture vector.' }
        $result = Get-Content -LiteralPath $global:RockwellOwnedStopFixture.ResourcePath -Raw | ConvertFrom-Json
        $result.state = 'terminated'; $result.receipt = [pscustomobject]@{ outcome = 'terminated' }
        $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $global:RockwellOwnedStopFixture.ResourcePath -Encoding UTF8
        return $true
    }
    $global:RockwellOwnedStopFixture.Process = $process
    $resource = [pscustomobject]@{ schema_version = 1; app_id = 'rockwell-mcp-server'; resource_id = ('b' * 32); owner_id = ('a' * 32);
        ownership = 'private'; containment = 'windows_job'; state = 'active'; recovery_barrier = $null; receipt = $null;
        process = [pscustomobject]@{ pid = 424242; executable = $image; created = $started.ToFileTimeUtc().ToString([Globalization.CultureInfo]::InvariantCulture) } }
    $resource | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resourcePath -Encoding UTF8
    [pscustomobject]@{ schemaVersion = 2; pid = 424241; servicePid = 424242; ownerId = ('a' * 32); resourceId = ('b' * 32); resourceRegistry = $registry } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $local 'management-supervisor.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $local 'service.pid') -Value '424242'
    Set-Content -LiteralPath (Join-Path $local 'service.key') -Value ('k' * 64)
    [pscustomobject]@{ schemaVersion = 1; ownerId = ('a' * 32); resourceId = ('b' * 32); requestId = ('c' * 32); signature = ('0' * 64) } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $local 'service-stop-request.json') -Encoding UTF8
    & $scriptPath | Out-Null
    if ($global:RockwellOwnedStopFixture.Waits -ne 1 -or $global:RockwellOwnedStopFixture.Disposals -ne 1) { throw 'Owner-mediated stop/disposal was not observed exactly once.' }
    $checks++
    try { & $scriptPath; throw 'A completed resource was stopped again.' }
    catch { if ($_.Exception.Message -notmatch 'active unclaimed private tree') { throw } }
    if ($global:RockwellOwnedStopFixture.Calls -ne 1) { throw 'A terminal resource triggered another PID lookup.' }
    $checks++
    $resource.receipt = $null; $resource.state = 'active'
    $resource.process.created = $started.AddTicks(-1).ToFileTimeUtc().ToString([Globalization.CultureInfo]::InvariantCulture)
    $resource | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resourcePath -Encoding UTF8
    Remove-Item -LiteralPath (Join-Path $local 'service-stop-request.json')
    try { & $scriptPath; throw 'A changed process identity was accepted.' }
    catch { if ($_.Exception.Message -notmatch 'identity changed') { throw } }
    if (Test-Path -LiteralPath (Join-Path $local 'service-stop-request.json')) { throw 'Changed identity produced a stop request.' }
    if ($global:RockwellOwnedStopFixture.Waits -ne 1) { throw 'Changed identity reached a process wait/stop.' }
    $checks++
    [pscustomobject]@{ passed = $checks; actualProcessStops = 0; actualServersStarted = 0 } | ConvertTo-Json -Compress
} finally {
    Remove-Variable -Name RockwellOwnedStopFixture -Scope Global -ErrorAction SilentlyContinue
    if (![IO.Path]::GetFullPath($root).StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup target escaped the temporary workspace.' }
    Remove-Item -LiteralPath $root -Recurse -Force
}
