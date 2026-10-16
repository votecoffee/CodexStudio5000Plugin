param([string[]]$AllowedRoot = @(), [switch]$EnableSdk, [int]$Port = 5487)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $repo 'service.local.json'
if (Test-Path -LiteralPath $configPath) {
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
} else {
    $config = Get-Content -LiteralPath (Join-Path $repo 'service.example.json') -Raw | ConvertFrom-Json
}
$config.ServiceUrl = "http://127.0.0.1:$Port"
if ($AllowedRoot.Count -gt 0) {
    $resolved = @($AllowedRoot | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
    $config.Studio5000.AllowedRoots = @(@($config.Studio5000.AllowedRoots) + @((Join-Path $repo 'examples')) + $resolved | Sort-Object -Unique)
}
if ($EnableSdk) {
    $bridge = Join-Path $repo 'artifacts/app/NativeBridge/Studio5000.NativeBridge.exe'
    if (!(Test-Path -LiteralPath $bridge)) { throw 'Run Build.ps1 -WithSdk first.' }
    $config.Studio5000.NativeBridgePath = $bridge
}
$config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding utf8
New-Item -ItemType Directory -Force -Path (Join-Path $repo '.local') | Out-Null
# Limit local credentials and working project files to this user and SYSTEM.
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls.exe (Join-Path $repo '.local') /inheritance:r /grant:r "*$($identity):(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not protect the local credential/state directory.' }
Write-Output "Configured $configPath"
