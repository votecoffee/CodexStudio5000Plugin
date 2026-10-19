param([switch]$WithSdk, [switch]$WithCip, [string]$ExtensionVersion = '0.21.0', [switch]$InstallExtensions)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repo
try {
    if ($InstallExtensions) { & (Join-Path $PSScriptRoot 'Assert-ApplicationsStopped.ps1') }
    & (Join-Path $PSScriptRoot 'New-AppIcon.ps1')
    dotnet build RockwellMCPServer.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    foreach ($name in @('Service','Mcp','Desktop','ExtensionWorker')) {
        dotnet publish "src/Studio5000.$name/Studio5000.$name.csproj" -c Release --no-restore -o "artifacts/app/$name" --nologo
        if ($LASTEXITCODE -ne 0) { throw "$name publish failed." }
    }
    if ($WithSdk) {
        dotnet publish src/Studio5000.NativeBridge/Studio5000.NativeBridge.csproj -c Release -o artifacts/app/NativeBridge --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Native bridge build failed. Install the Logix Designer SDK local NuGet package.' }
    }
    if ($WithCip) {
        dotnet publish src/Studio5000.CipBridge/Studio5000.CipBridge.csproj -c Release -o artifacts/app/CipBridge --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Optional CIP bridge build failed.' }
    }
    & (Join-Path $PSScriptRoot 'Build-Extensions.ps1') -Version $ExtensionVersion -ContentVersion:$InstallExtensions -SelectOnStartup:$InstallExtensions
} finally { Pop-Location }
