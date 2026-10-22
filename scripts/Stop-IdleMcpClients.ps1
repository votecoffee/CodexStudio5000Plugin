param([Parameter(Mandatory=$true)][IO.FileStream]$UpdateLock)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$lockPath = [IO.Path]::GetFullPath((Join-Path $repo '.local/application-update.lock'))
if (!$UpdateLock.CanWrite -or !$UpdateLock.Name.Equals($lockPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Idle frontend refresh requires the installer-owned update lock.'
}
$appRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/app')) + [IO.Path]::DirectorySeparatorChar
$mcpPath = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/app/Mcp/Studio5000.Mcp.exe'))
$running = @(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($appRoot, [StringComparison]::OrdinalIgnoreCase) })
if (@($running | Where-Object { !$_.Path.Equals($mcpPath, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
    throw 'The service, GUI or engineering workers are running. Finish their work and stop them before reconnecting MCP clients.'
}
if (!$running.Count) { return }
$configFile = Join-Path $repo 'service.local.json'
if (!(Test-Path -LiteralPath $configFile)) { throw 'Cannot verify that the configured service is stopped.' }
$settings = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
$endpoint = [uri]$settings.ServiceUrl
$address = $null
if ($endpoint.Scheme -ne 'http' -or ![Net.IPAddress]::TryParse($endpoint.Host, [ref]$address) -or ![Net.IPAddress]::IsLoopback($address)) {
    throw 'Idle frontend refresh requires the existing literal loopback service endpoint.'
}
$probe = [Net.Sockets.TcpClient]::new()
$refused = $false
try {
    $connect = $probe.ConnectAsync($endpoint.Host, $endpoint.Port)
    try { $finished = $connect.Wait(5000) }
    catch {
        $errorDetail = $_.Exception
        while ($errorDetail.InnerException) { $errorDetail = $errorDetail.InnerException }
        if ($errorDetail -is [Net.Sockets.SocketException] -and $errorDetail.SocketErrorCode -eq [Net.Sockets.SocketError]::ConnectionRefused) { $refused = $true }
        else { throw 'Service availability is uncertain; no frontend was stopped.' }
    }
    if (!$refused) { throw 'The service endpoint is listening or its state is uncertain; no frontend was stopped.' }
} finally { $probe.Dispose() }
# These thin transports retain no engineering state. Never stop another executable,
# a stale PID, the shared service, a native GUI or a worker to make an update fit.
foreach ($snapshot in $running) {
    $current = Get-Process -Id $snapshot.Id -ErrorAction SilentlyContinue
    if (!$current) { continue }
    if (!$current.Path -or !$current.Path.Equals($mcpPath, [StringComparison]::OrdinalIgnoreCase) -or $current.StartTime.Ticks -ne $snapshot.StartTime.Ticks) {
        throw 'A frontend identity changed; no further processes were stopped.'
    }
    Stop-Process -Id $current.Id -ErrorAction Stop
    if (!$current.WaitForExit(3000)) { throw 'A frontend is still stopping; installation cannot continue.' }
}
Write-Output "Stopped $($running.Count) disconnected MCP frontends for the requested update. Reconnect clients after installation."
