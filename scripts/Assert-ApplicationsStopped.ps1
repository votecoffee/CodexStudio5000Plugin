$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$appRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/app')) + [IO.Path]::DirectorySeparatorChar
$running = @(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($appRoot, [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) {
    throw 'Applications from this checkout are running. Finish jobs, stop watch polling, close its clients and run scripts/Stop-Service.ps1 before installing updates.'
}
