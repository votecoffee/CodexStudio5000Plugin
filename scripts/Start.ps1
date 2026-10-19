param([string]$ProjectPath, [switch]$ShowErrorDialog)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
try {
    foreach ($name in @('Service', 'Desktop')) {
        if (!(Test-Path -LiteralPath (Join-Path $repo "artifacts/app/$name/Studio5000.$name.exe") -PathType Leaf)) {
            throw 'The application is not installed. Run Install.cmd in the project root first.'
        }
    }
    & (Join-Path $PSScriptRoot 'Start-Service.ps1') -Background
    if ($ProjectPath) { & (Join-Path $PSScriptRoot 'Start-Gui.ps1') -ProjectPath $ProjectPath }
    else { & (Join-Path $PSScriptRoot 'Start-Gui.ps1') }
} catch {
    if ($ShowErrorDialog) {
        # A Start Menu launch has no console, so keep startup failures visible.
        Add-Type -AssemblyName System.Windows.Forms
        [void][System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'RockwellMCPServer could not start', 'OK', 'Error')
    }
    throw
}
