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
