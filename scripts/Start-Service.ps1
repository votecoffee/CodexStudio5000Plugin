param([switch]$Background)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $repo '.local/maintenance-hold.json')) { throw 'RockwellMCPServer is on operator maintenance hold. Obtain explicit human authorization before clearing the hold and starting the service.' }
$updateLockPath = Join-Path $repo '.local/application-update.lock'
if (Test-Path -LiteralPath $updateLockPath) {
    try { $probe = [IO.File]::Open($updateLockPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $probe.Dispose() }
    catch [IO.IOException] { throw 'RockwellMCPServer is being updated. Start the service after installation finishes.' }
}
$exe = Join-Path $repo 'artifacts/app/Service/Studio5000.Service.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Run scripts/Build.ps1 first.' }
if (!(Test-Path -LiteralPath (Join-Path $repo 'service.local.json'))) { & (Join-Path $PSScriptRoot 'Initialize.ps1') }
$env:STUDIO5000_PLUGIN_ROOT = $repo
$management = $null
$managementPointer = Join-Path $repo '.local/management-runtime.json'
if (Test-Path -LiteralPath $managementPointer) {
    $management = Get-Content -LiteralPath $managementPointer -Raw | ConvertFrom-Json
    if ($management.schemaVersion -ne 1 -or !$management.python -or !(Test-Path -LiteralPath $management.python -PathType Leaf) -or
        (Get-FileHash -LiteralPath $management.python).Hash -ne $management.pythonSha256) { throw 'Management runtime pointer/hash is invalid; select a verified app-owned runtime.' }
}
if ($Background) {
    $local = Join-Path $repo '.local'
    $pidFile = Join-Path $local 'service.pid'
    if (Test-Path -LiteralPath $pidFile) {
        $existing = Get-Process -Id ([int](Get-Content -LiteralPath $pidFile -Raw).Trim()) -ErrorAction SilentlyContinue
        if ($existing -and $existing.Path -and $existing.Path.Equals($exe, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Output "Shared service is already running as PID $($existing.Id)."
            return
        }
    }
    if ($management) {
        $stdin = Join-Path $local 'management.stdin'
        [IO.File]::WriteAllText($stdin, '')
        $arguments = @('-m', 'rockwell_framework.management', '--app-root', ('"' + $repo + '"'))
        if ($management.registry) { $arguments += @('--registry', ('"' + $management.registry + '"')) }
        $process = Start-Process -FilePath $management.python -ArgumentList $arguments -WorkingDirectory $repo -WindowStyle Hidden -PassThru -RedirectStandardInput $stdin -RedirectStandardOutput (Join-Path $local 'management.stdout.log') -RedirectStandardError (Join-Path $local 'management.stderr.log')
    } else {
        $process = Start-Process -FilePath $exe -WorkingDirectory $repo -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $local 'service.stdout.log') -RedirectStandardError (Join-Path $local 'service.stderr.log')
        $process.Id | Set-Content -LiteralPath (Join-Path $local 'service.pid')
    }
    $settings = Get-Content -LiteralPath (Join-Path $repo 'service.local.json') -Raw | ConvertFrom-Json
    $ready = $false
    for ($attempt=0; $attempt -lt 150; $attempt++) {
        if ($process.HasExited) { throw 'Service/supervisor exited during startup; inspect .local/management.stderr.log and service.stderr.log.' }
        try { $health=Invoke-RestMethod -Uri ($settings.ServiceUrl+'/health') -TimeoutSec 1; if ($health.status -eq 'ok') { $ready=$true; break } } catch { }
        Start-Sleep -Milliseconds 200
    }
    if (!$ready) { throw 'Service did not become ready; inspect .local/service.stderr.log.' }
    Write-Output "Started shared service PID $(([IO.File]::ReadAllText($pidFile)).Trim())."
} elseif ($management) {
    $arguments = @('-m', 'rockwell_framework.management', '--app-root', $repo)
    if ($management.registry) { $arguments += @('--registry', $management.registry) }
    & $management.python @arguments
} else { & $exe }
