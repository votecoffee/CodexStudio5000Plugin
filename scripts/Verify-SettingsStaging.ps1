param([Parameter(Mandatory=$true)][string]$ServiceExe,
    [string]$ResultsDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/test-results/settings-staging-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$ServiceExe = [IO.Path]::GetFullPath($ServiceExe)
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
$fixture = Join-Path $ResultsDirectory 'generated settings only'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$document = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'service.example.json') -Raw | ConvertFrom-Json
$document.Studio5000.StateDirectory = 'state-must-not-be-opened'
$document.Studio5000.ControllerTargets = @(@{ Id = 'generated'; CommunicationsPath = 'AB_ETH-1\192.0.2.10\Backplane\0'; Policy = @{ AllowedMethods = @('GoOnlineAsync'); ProgramName = 'Test'; RoutineName = 'main' } })
$config = Join-Path $fixture 'service.local.json'
$document | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $config -Encoding utf8
$beforeHash = (Get-FileHash -LiteralPath $config).Hash
$previousRoot = $env:STUDIO5000_PLUGIN_ROOT
$process = $null
try {
    $env:STUDIO5000_PLUGIN_ROOT = $fixture
    $stdout = Join-Path $ResultsDirectory 'stage.stdout.json'
    $stderr = Join-Path $ResultsDirectory 'stage.stderr.log'
    $process = Start-Process -FilePath $ServiceExe -ArgumentList '--stage-broad-local-settings' -WorkingDirectory $fixture -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (!$process.WaitForExit(30000)) { throw 'Settings staging helper exceeded 30 seconds.' }
    if ($process.ExitCode -ne 0) { throw 'Settings staging helper failed; inspect its private stderr log.' }
    $receipt = Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json
    $saved = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    if (!$receipt.saved -or !$receipt.restartRequired -or $receipt.nativeConnectionAttempted -or $receipt.runningServiceChanged) { throw 'Staging receipt has unexpected effects.' }
    if (!$saved.Studio5000.EnableControllerOperations -or !$saved.Studio5000.AllowAnyControllerPath -or !$saved.Studio5000.AllowAnySourcePath -or !$saved.Studio5000.AllowAnyOutputPath -or
        $null -ne $saved.Studio5000.ControllerTargets[0].Policy) { throw 'Broad local settings were not staged completely.' }
    if ((Get-FileHash -LiteralPath $receipt.backupPath).Hash -ne $beforeHash) { throw 'Settings backup does not preserve original bytes.' }
    if ((Test-Path -LiteralPath (Join-Path $fixture 'state-must-not-be-opened')) -or (Test-Path -LiteralPath (Join-Path $fixture '.local/service.key')) -or
        (Test-Path -LiteralPath (Join-Path $fixture '.local/connection.json'))) { throw 'Staging unexpectedly initialized a service or workspace.' }
    # Separately exercise real host binding: explicitly empty arrays must not
    # restore default roots or report a false pending restart after startup.
    $startup = Join-Path $ResultsDirectory 'generated empty-root startup'
    New-Item -ItemType Directory -Path $startup -Force | Out-Null
    $empty = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'service.example.json') -Raw | ConvertFrom-Json
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
    $empty.ServiceUrl = "http://127.0.0.1:$port"
    $empty.Studio5000.AllowedRoots = @(); $empty.Studio5000.OutputRoots = @(); $empty.Studio5000.AllowAnySourcePath = $true
    $empty | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $startup 'service.local.json') -Encoding utf8
    $env:STUDIO5000_PLUGIN_ROOT = $startup
    $process = Start-Process -FilePath $ServiceExe -WorkingDirectory $startup -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $ResultsDirectory 'startup.stdout.log') -RedirectStandardError (Join-Path $ResultsDirectory 'startup.stderr.log')
    $ready = $false
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        if ($process.HasExited) { throw 'Empty-root fixture host exited; inspect its private startup log.' }
        try { $health = Invoke-RestMethod -Uri ($empty.ServiceUrl + '/health') -TimeoutSec 1; if ($health.status -eq 'ok') { $ready = $true; break } } catch { }
        Start-Sleep -Milliseconds 100
    }
    if (!$ready) { throw 'Empty-root fixture host did not start.' }
    $connection = Get-Content -LiteralPath (Join-Path $startup '.local/connection.json') -Raw | ConvertFrom-Json
    $headers = @{ Authorization = 'Bearer ' + ([IO.File]::ReadAllText($connection.tokenFile)).Trim() }
    $snapshot = Invoke-RestMethod -Uri ($connection.url + '/api/settings') -Headers $headers -TimeoutSec 5
    if ($snapshot.restartRequired -or @($snapshot.effective.Studio5000.AllowedRoots).Count -ne 0 -or @($snapshot.effective.Studio5000.OutputRoots).Count -ne 0 -or
        $snapshot.effective.Studio5000.EnableControllerOperations -or $snapshot.effective.Studio5000.NativeBridgePath) { throw 'Host binding changed empty arrays or the disabled native policy.' }
    [pscustomobject]@{ broadPolicyStaged = $true; originalBackupVerified = $true; stagingDidNotStartWorkspace = $true;
        isolatedHostPreservesEmptyRoots = $true; nativeConnectionAttempted = $false } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.json') -Encoding utf8
    Write-Output "Offline settings staging passed: $ResultsDirectory"
} finally {
    if ($null -ne $process -and !$process.HasExited) { Stop-Process -InputObject $process; $process.WaitForExit() }
    $env:STUDIO5000_PLUGIN_ROOT = $previousRoot
}
