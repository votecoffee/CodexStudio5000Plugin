param([switch]$WithSdk, [switch]$WithCip, [switch]$SkipBuild, [switch]$NoShortcut)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $repo 'service.local.json'

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
    # Initialize defaults to port 5487; retain the configured port during an update.
    $settings = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    & (Join-Path $PSScriptRoot 'Initialize.ps1') -EnableSdk:$WithSdk -EnableCip:$WithCip -Port ([uri]$settings.ServiceUrl).Port
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
Write-Output 'Installation complete. Run Run.cmd or open RockwellMCPServer from the Start Menu.'
