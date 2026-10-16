$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$pidFile = Join-Path $repo '.local/service.pid'
if (!(Test-Path -LiteralPath $pidFile)) { throw 'No background service PID file was found.' }
$serviceProcessId = [int](Get-Content -LiteralPath $pidFile -Raw).Trim()
$process = Get-Process -Id $serviceProcessId -ErrorAction SilentlyContinue
if (!$process) { Write-Output 'The recorded service process has stopped.'; exit 0 }
$expected = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/app/Service/Studio5000.Service.exe'))
if (!$process.Path -or !([IO.Path]::GetFullPath($process.Path).Equals($expected, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Recorded PID belongs to a different executable; refusing to stop it.'
}
Stop-Process -Id $serviceProcessId
$process.WaitForExit()
Write-Output "Stopped shared service PID $serviceProcessId."
