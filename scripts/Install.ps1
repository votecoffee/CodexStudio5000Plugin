[CmdletBinding()]
param([switch]$WithSdk, [switch]$WithCip, [switch]$SkipBuild, [switch]$NoShortcut, [switch]$ReconnectIdleMcpClients,
    [ValidateSet('Standalone','None')][string]$CodexIntegration = 'Standalone')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $repo '.local/maintenance-hold.json')) { throw 'RockwellMCPServer is on operator maintenance hold. Do not reinstall or reconnect until the human explicitly releases the hold.' }
$configPath = Join-Path $repo 'service.local.json'
$localDirectory = Join-Path $repo '.local'
New-Item -ItemType Directory -Path $localDirectory -Force | Out-Null
$updateLock = $null
try { $updateLock = [IO.File]::Open((Join-Path $localDirectory 'application-update.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
catch [IO.IOException] { throw 'Another RockwellMCPServer update is in progress. Wait for it to finish before installing.' }
try {

if ($ReconnectIdleMcpClients) { & (Join-Path $PSScriptRoot 'Stop-IdleMcpClients.ps1') -UpdateLock $updateLock }
if (!$SkipBuild -or $WithSdk -or $WithCip) {
    & (Join-Path $PSScriptRoot 'Assert-ApplicationsStopped.ps1')
}
if (!$SkipBuild) {
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 8 SDK, then run Install.cmd again.' }
    $sdks = @(dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0 -or !($sdks -match '^8\.')) { throw 'Install the .NET 8 SDK, then run Install.cmd again.' }
    & (Join-Path $PSScriptRoot 'Build.ps1') -WithSdk:$WithSdk -WithCip:$WithCip -InstallExtensions
}
foreach ($name in @('Service', 'Mcp', 'Desktop', 'ExtensionWorker')) {
    if (!(Test-Path -LiteralPath (Join-Path $repo "artifacts/app/$name/Studio5000.$name.exe") -PathType Leaf)) {
        throw "Missing $name application. Run Install.cmd without -SkipBuild."
    }
}

if (!(Test-Path -LiteralPath $configPath)) {
    & (Join-Path $PSScriptRoot 'Initialize.ps1') -EnableSdk:$WithSdk -EnableCip:$WithCip
} elseif ($WithSdk -or $WithCip) {
    # Rebuilding already configured bridges must preserve exact settings bytes.
    $settings = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $enableSdk = $WithSdk -and (!$settings.Studio5000.NativeBridgePath -or ![IO.Path]::GetFullPath([IO.Path]::Combine($repo, $settings.Studio5000.NativeBridgePath)).Equals([IO.Path]::GetFullPath((Join-Path $repo 'artifacts/app/NativeBridge/Studio5000.NativeBridge.exe')), [StringComparison]::OrdinalIgnoreCase))
    $enableCip = $WithCip -and (!$settings.Studio5000.CipBridgePath -or ![IO.Path]::GetFullPath([IO.Path]::Combine($repo, $settings.Studio5000.CipBridgePath)).Equals([IO.Path]::GetFullPath((Join-Path $repo 'artifacts/app/CipBridge/Studio5000.CipBridge.exe')), [StringComparison]::OrdinalIgnoreCase))
    if ($enableSdk -or $enableCip) { & (Join-Path $PSScriptRoot 'Initialize.ps1') -EnableSdk:$enableSdk -EnableCip:$enableCip -Port ([uri]$settings.ServiceUrl).Port }
    else { Write-Output 'Preserved existing service.local.json and configured SDK/CIP paths.' }
} else {
    Write-Output 'Preserved existing service.local.json.'
}

if (!$NoShortcut) {
    $iconPath = Join-Path $repo 'assets/icons/RockwellMCPServer.ico'
    if (!(Test-Path -LiteralPath $iconPath -PathType Leaf)) { throw 'The application icon is missing. Run Install.cmd without -SkipBuild.' }
    $programs = [Environment]::GetFolderPath('Programs')
    if (!$programs) { throw 'The current-user Start Menu folder is unavailable. Use -NoShortcut to install without a shortcut.' }
    New-Item -ItemType Directory -Path $programs -Force | Out-Null
    $shortcutPath = Join-Path $programs 'RockwellMCPServer.lnk'
    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
        $shortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $PSScriptRoot 'Start.ps1') + '" -ShowErrorDialog'
        $shortcut.WorkingDirectory = $repo
        $shortcut.IconLocation = $iconPath + ',0'
        $shortcut.Description = 'Start the RockwellMCPServer shared service and desktop GUI'
        $shortcut.Save()
    } finally {
        if ($shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        if ($shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
    Write-Output "Created Start Menu shortcut: $shortcutPath"
}
& (Join-Path $PSScriptRoot 'Configure-Codex.ps1') -Route $CodexIntegration
Write-Output 'Installation complete. Run Run.cmd or open RockwellMCPServer from the Start Menu.'
} finally { if ($updateLock) { $updateLock.Dispose() } }
