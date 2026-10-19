param([switch]$WithSdk, [string]$ExtensionVersion = '0.9.0')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repo
try {
    dotnet build RockwellMCPServer.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    foreach ($name in @('Service','Mcp','Desktop','ExtensionWorker')) {
        dotnet publish "src/Studio5000.$name/Studio5000.$name.csproj" -c Release --no-restore -o "artifacts/app/$name" --nologo
        if ($LASTEXITCODE -ne 0) { throw "$name publish failed." }
    }
    & (Join-Path $PSScriptRoot 'Build-Extensions.ps1') -Version $ExtensionVersion
    if ($WithSdk) {
        dotnet publish src/Studio5000.NativeBridge/Studio5000.NativeBridge.csproj -c Release -o artifacts/app/NativeBridge --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Native bridge build failed. Install the Logix Designer SDK local NuGet package.' }
    }
} finally { Pop-Location }
