param([switch]$ReviewBlockedStop)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$pidFile = Join-Path $repo '.local/service.pid'
if (!(Test-Path -LiteralPath $pidFile)) { throw 'No background service PID file was found.' }
$serviceProcessId = [int](Get-Content -LiteralPath $pidFile -Raw).Trim()
$supervisorFile = Join-Path $repo '.local/management-supervisor.json'
if (Test-Path -LiteralPath $supervisorFile) {
    $owned = Get-Content -LiteralPath $supervisorFile -Raw | ConvertFrom-Json
    if ($owned.schemaVersion -eq 2) {
        if ($owned.servicePid -ne $serviceProcessId -or $owned.ownerId -notmatch '^[0-9a-f]{32}$' -or
            $owned.resourceId -notmatch '^[0-9a-f]{32}$' -or !$owned.resourceRegistry) { throw 'Managed ownership receipt differs; no stop was requested.' }
        $resourcePath = Join-Path ([IO.Path]::GetFullPath($owned.resourceRegistry)) ('resources/' + $owned.resourceId + '.json')
        $resource = Get-Content -LiteralPath $resourcePath -Raw | ConvertFrom-Json
        if ($resource.schema_version -ne 1 -or $resource.app_id -ne 'rockwell-mcp-server' -or
            $resource.resource_id -ne $owned.resourceId -or $resource.owner_id -ne $owned.ownerId -or
            $resource.ownership -ne 'private' -or $resource.containment -ne 'windows_job' -or
            $resource.process.pid -ne $serviceProcessId -or $resource.state -ne 'active' -or
            $resource.recovery_barrier -or $resource.receipt) { throw 'Managed resource is not an active unclaimed private tree; inspect its retained record.' }
        $ownedProcess = Get-Process -Id $serviceProcessId -ErrorAction SilentlyContinue
        if (!$ownedProcess) { throw 'Recorded service is gone; inspect the retained resource/cleanup outcome instead of replaying stop.' }
        try {
            [void]$ownedProcess.Handle # Pin before checking creation identity.
            $expectedImage = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/app/Service/Studio5000.Service.exe'))
            if ($ownedProcess.HasExited -or !$ownedProcess.Path -or
                !([IO.Path]::GetFullPath($ownedProcess.Path).Equals($expectedImage, [StringComparison]::OrdinalIgnoreCase)) -or
                !([IO.Path]::GetFullPath($resource.process.executable).Equals($expectedImage, [StringComparison]::OrdinalIgnoreCase)) -or
                $ownedProcess.StartTime.ToUniversalTime().ToFileTimeUtc().ToString([Globalization.CultureInfo]::InvariantCulture) -cne [string]$resource.process.created) {
                throw 'Managed process identity changed; no stop was requested.'
            }
            $key = [IO.File]::ReadAllText((Join-Path $repo '.local/service.key')).TrimStart([char]0xfeff).Trim()
            if ($key.Length -lt 32 -or $key.Length -gt 4096) { throw 'Private stop authentication is unavailable.' }
            $requestPath = Join-Path $repo '.local/service-stop-request.json'
            $requestId = [Guid]::NewGuid().ToString('N')
            if (Test-Path -LiteralPath $requestPath) {
                $prior = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
                if ($prior.ownerId -eq $owned.ownerId -and $prior.resourceId -eq $owned.resourceId) {
                    if ($prior.requestId -notmatch '^[0-9a-f]{32}$') { throw 'Existing stop request is invalid; preserve it for inspection.' }
                    $requestId = $prior.requestId # Observe the same intent, never replace its ID.
                    $priorReplyPath = Join-Path $repo '.local/service-stop-result.json'
                    if (Test-Path -LiteralPath $priorReplyPath) {
                        $priorReply = Get-Content -LiteralPath $priorReplyPath -Raw | ConvertFrom-Json
                        if ($priorReply.ownerId -eq $owned.ownerId -and $priorReply.resourceId -eq $owned.resourceId -and $priorReply.requestId -eq $requestId) {
                            if ($priorReply.state -eq 'blocked' -and $ReviewBlockedStop) { $requestId = [Guid]::NewGuid().ToString('N') }
                            elseif ($priorReply.state -in @('blocked','uncertain','completed')) { throw ('Prior owner stop '+$priorReply.state+'; inspect it. -ReviewBlockedStop applies only after resolving a verified zero-effect refusal.') }
                        }
                    }
                }
            }
            $body = "rockwell-stop-v1`n$($owned.ownerId)`n$($owned.resourceId)`n$requestId"
            $hmac = New-Object System.Security.Cryptography.HMACSHA256
            try { $hmac.Key = [Text.Encoding]::UTF8.GetBytes($key); $signature = -join ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($body)) | ForEach-Object { $_.ToString('x2') }) }
            finally { $hmac.Dispose() }
            $request = [ordered]@{ schemaVersion = 1; ownerId = $owned.ownerId; resourceId = $owned.resourceId; requestId = $requestId; signature = $signature }
            $temporary = $requestPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
            try {
                [IO.File]::WriteAllText($temporary, ($request | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding($false)))
                if (Test-Path -LiteralPath $requestPath) { [IO.File]::Replace($temporary, $requestPath, [NullString]::Value) } else { [IO.File]::Move($temporary, $requestPath) }
            } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
            $waitDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
            while (!$ownedProcess.WaitForExit(100)) {
                $replyPath = Join-Path $repo '.local/service-stop-result.json'
                if (Test-Path -LiteralPath $replyPath) {
                    $reply = Get-Content -LiteralPath $replyPath -Raw | ConvertFrom-Json
                    if ($reply.ownerId -eq $owned.ownerId -and $reply.resourceId -eq $owned.resourceId -and $reply.requestId -eq $requestId -and $reply.state -in @('blocked','uncertain')) {
                        throw ('Owner stop '+$reply.state+': '+$reply.message+' Inspect the retained request/resource; no PID was stopped.')
                    }
                }
                if ([DateTimeOffset]::UtcNow -ge $waitDeadline) { throw 'Owner stop has no confirmed terminal outcome; inspect the same request/resource and do not retry with another ID.' }
            }
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
            do {
                $result = Get-Content -LiteralPath $resourcePath -Raw | ConvertFrom-Json
                if ($result.receipt -and $result.receipt.outcome -in @('terminated','already_exited')) { Write-Output 'Managed owner confirmed resource-tree cleanup.'; return }
                if ($result.receipt -and $result.receipt.outcome -eq 'uncertain') { throw 'Resource cleanup is uncertain; preserve the receipt and do not replay.' }
                Start-Sleep -Milliseconds 100
            } while ([DateTimeOffset]::UtcNow -lt $deadline)
            throw 'Service exited without a verified cleanup receipt; inspect retained ownership evidence.'
        } finally { $ownedProcess.Dispose() }
    }
}
$ownerPath = Join-Path $repo '.local/service-owner.json'
if (!(Test-Path -LiteralPath $ownerPath)) { throw 'No creation-bound service ownership receipt exists. Do not terminate from this legacy PID file; inspect the old instance manually.' }
$direct = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
if ($direct.schemaVersion -ne 1 -or $direct.ownerId -notmatch '^[0-9a-f]{32}$' -or $direct.owner.pid -ne $serviceProcessId) { throw 'Direct service ownership generation differs; no stop requested.' }
$baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
try { $cryptoKey = $baseKey.OpenSubKey('SOFTWARE\Microsoft\Cryptography'); try { $machine = ([Guid]::Parse([string]$cryptoKey.GetValue('MachineGuid'))).ToString('D').ToLowerInvariant() } finally { if ($cryptoKey) { $cryptoKey.Dispose() } } } finally { $baseKey.Dispose() }
$sha = [Security.Cryptography.SHA256]::Create()
try { $hostId = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($machine)) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
if ($direct.owner.host_id -cne $hostId) { throw 'Foreign/unverifiable service ownership host; no local PID lookup is permitted.' }
$process = Get-Process -Id $serviceProcessId -ErrorAction SilentlyContinue
if (!$process) { throw 'Recorded service is gone; inspect retained resource outcomes instead of replaying stop.' }
try {
    [void]$process.Handle
    $expected = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/app/Service/Studio5000.Service.exe'))
    if ($process.HasExited -or !$process.Path -or !([IO.Path]::GetFullPath($process.Path).Equals($expected, [StringComparison]::OrdinalIgnoreCase)) -or
        !([IO.Path]::GetFullPath($direct.owner.executable).Equals($expected, [StringComparison]::OrdinalIgnoreCase)) -or
        $process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString([Globalization.CultureInfo]::InvariantCulture) -cne [string]$direct.owner.created) { throw 'Direct service process identity changed; no stop requested.' }
    $requestPath = Join-Path $repo '.local/service-owner-stop.json'; $replyPath = Join-Path $repo '.local/service-owner-stop-result.json'
    $requestId = [Guid]::NewGuid().ToString('N')
    if (Test-Path -LiteralPath $requestPath) {
        $prior = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
        if ($prior.ownerId -eq $direct.ownerId) {
            $requestId = $prior.requestId
            if (Test-Path -LiteralPath $replyPath) {
                $reply = Get-Content -LiteralPath $replyPath -Raw | ConvertFrom-Json
                if ($reply.ownerId -eq $direct.ownerId -and $reply.requestId -eq $requestId -and $reply.state -eq 'blocked' -and $ReviewBlockedStop) { $requestId = [Guid]::NewGuid().ToString('N') }
                elseif ($reply.ownerId -eq $direct.ownerId -and $reply.requestId -eq $requestId -and $reply.state -in @('blocked','uncertain','completed')) { throw ('Prior stop '+$reply.state+'; inspect its outcome. Use -ReviewBlockedStop only after a verified zero-effect refusal is resolved.') }
            }
        }
    }
    if ($requestId -notmatch '^[0-9a-f]{32}$') { throw 'Retained stop request identity is invalid.' }
    $key = [IO.File]::ReadAllText((Join-Path $repo '.local/service.key')).TrimStart([char]0xfeff).Trim()
    if ($key.Length -lt 32 -or $key.Length -gt 4096) { throw 'Private stop authentication is unavailable.' }
    $body = "rockwell-stop-v1`n$($direct.ownerId)`n$($direct.ownerId)`n$requestId"
    $hmac = New-Object Security.Cryptography.HMACSHA256
    try { $hmac.Key = [Text.Encoding]::UTF8.GetBytes($key); $signature = -join ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($body)) | ForEach-Object { $_.ToString('x2') }) } finally { $hmac.Dispose() }
    $value = [ordered]@{ schemaVersion=1; ownerId=$direct.ownerId; resourceId=$direct.ownerId; requestId=$requestId; signature=$signature }
    $temporary = $requestPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try { [IO.File]::WriteAllText($temporary, ($value | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding($false))); if (Test-Path -LiteralPath $requestPath) { [IO.File]::Replace($temporary,$requestPath,[NullString]::Value) } else { [IO.File]::Move($temporary,$requestPath) } }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while (!$process.WaitForExit(100)) {
        if (Test-Path -LiteralPath $replyPath) {
            $reply = Get-Content -LiteralPath $replyPath -Raw | ConvertFrom-Json
            if ($reply.ownerId -eq $direct.ownerId -and $reply.requestId -eq $requestId -and $reply.state -in @('blocked','uncertain')) { throw ('Direct service stop '+$reply.state+': '+$reply.message) }
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Direct stop outcome is not confirmed; inspect the same intent instead of creating another request.' }
    }
    if (!(Test-Path -LiteralPath $replyPath)) { throw 'Service exited without an owned shutdown receipt; retain the uncertain outcome.' }
    $reply = Get-Content -LiteralPath $replyPath -Raw | ConvertFrom-Json
    if ($reply.ownerId -ne $direct.ownerId -or $reply.requestId -ne $requestId -or $reply.state -ne 'completed') { throw 'Service exit lacks matching verified shutdown evidence.' }
    Write-Output 'Direct service owner confirmed cleanup and shutdown.'
} finally { $process.Dispose() }
