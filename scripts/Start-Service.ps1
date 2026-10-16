param([switch]$Background)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'artifacts/app/Service/Studio5000.Service.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Run scripts/Build.ps1 first.' }
if (!(Test-Path -LiteralPath (Join-Path $repo 'service.local.json'))) { & (Join-Path $PSScriptRoot 'Initialize.ps1') }
$env:STUDIO5000_PLUGIN_ROOT = $repo
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
    $process = Start-Process -FilePath $exe -WorkingDirectory $repo -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $local 'service.stdout.log') -RedirectStandardError (Join-Path $local 'service.stderr.log')
    $process.Id | Set-Content -LiteralPath (Join-Path $local 'service.pid')
    $settings = Get-Content -LiteralPath (Join-Path $repo 'service.local.json') -Raw | ConvertFrom-Json
    $ready = $false
    for ($attempt=0; $attempt -lt 75; $attempt++) {
        if ($process.HasExited) { throw 'Service exited during startup; inspect .local/service.stderr.log.' }
        try { $health=Invoke-RestMethod -Uri ($settings.ServiceUrl+'/health') -TimeoutSec 1; if ($health.status -eq 'ok') { $ready=$true; break } } catch { }
        Start-Sleep -Milliseconds 200
    }
    if (!$ready) { throw 'Service did not become ready; inspect .local/service.stderr.log.' }
    Write-Output "Started shared service PID $($process.Id)."
} else { & $exe }
