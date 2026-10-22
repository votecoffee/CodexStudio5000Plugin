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
            if (!$ownedProcess.WaitForExit(20000)) { throw 'Owner stop has no confirmed terminal outcome; inspect the same request/resource and do not retry with another ID.' }
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
$process = Get-Process -Id $serviceProcessId -ErrorAction SilentlyContinue
if (!$process) { Write-Output 'The recorded service process has stopped.'; exit 0 }
$expected = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/app/Service/Studio5000.Service.exe'))
if (!$process.Path -or !([IO.Path]::GetFullPath($process.Path).Equals($expected, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Recorded PID belongs to a different executable; refusing to stop it.'
}
$parentProcessId = (Get-CimInstance Win32_Process -Filter "ProcessId = $serviceProcessId").ParentProcessId
Stop-Process -Id $serviceProcessId
$process.WaitForExit()
Write-Output "Stopped shared service PID $serviceProcessId."
$supervisorFile = Join-Path $repo '.local/management-supervisor.json'
if (Test-Path -LiteralPath $supervisorFile) {
    $record = Get-Content -LiteralPath $supervisorFile -Raw | ConvertFrom-Json
    if ($record.servicePid -eq $serviceProcessId -and $record.pid -eq $parentProcessId) {
        $supervisor = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
        if ($supervisor -and $supervisor.ProcessName -in @('python','pythonw')) {
            if (!$supervisor.WaitForExit(5000)) { throw 'The reporting supervisor is still stopping; wait before restarting the service.' }
        }
    }
}
