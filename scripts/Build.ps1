param([switch]$WithSdk, [switch]$WithCip, [string]$ExtensionVersion, [switch]$InstallExtensions)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Stamp-Release.ps1') -Check | Out-Null
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
$release = Get-RockwellRelease $repo
if (!$ExtensionVersion) { $ExtensionVersion = $release.packageVersion }
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
    # Publish beside the SDK adapter so the immutable closure includes both.
    dotnet publish src/Studio5000.StdioHost/Studio5000.StdioHost.csproj -c Release --no-restore -o artifacts/app/Mcp --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Stdio owner publish failed.' }
    # This rebuild selects the just-published ordinary service, not a native
    # fallback. Preserve an earlier generated pin before creating the new one.
    $offlinePin = Join-Path $repo 'artifacts/app/Mcp/offline-service.json'
    if (Test-Path -LiteralPath $offlinePin) {
        $pinHistory = Join-Path $repo 'artifacts/offline-pin-history'
        New-Item -ItemType Directory -Path $pinHistory -Force > $null
        Copy-Item -LiteralPath $offlinePin -Destination (Join-Path $pinHistory ([Guid]::NewGuid().ToString('N') + '.json'))
        Remove-Item -LiteralPath $offlinePin
    }
    & (Join-Path $PSScriptRoot 'New-OfflineServicePin.ps1') -AppRoot $repo -Destination $offlinePin -ExpectedServiceVersion $release.displayVersion
    if ($WithSdk) {
        dotnet publish src/Studio5000.NativeBridge/Studio5000.NativeBridge.csproj -c Release -o artifacts/app/NativeBridge --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Native bridge build failed. Install the Logix Designer SDK local NuGet package.' }
    }
    if ($WithCip) {
        dotnet publish src/Studio5000.CipBridge/Studio5000.CipBridge.csproj -c Release -o artifacts/app/CipBridge --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Optional CIP bridge build failed.' }
    }
    & (Join-Path $PSScriptRoot 'Build-Extensions.ps1') -Version $ExtensionVersion -ContentVersion:$InstallExtensions -SelectOnStartup:$InstallExtensions
    $manifest = Get-Content -LiteralPath (Join-Path $repo 'plugin.json') -Raw | ConvertFrom-Json
    $lock = Get-Content -LiteralPath (Join-Path $repo 'src/Studio5000.Mcp/packages.lock.json') -Raw | ConvertFrom-Json
    $sdk = @($lock.dependencies.PSObject.Properties.Value | ForEach-Object { $_.ModelContextProtocol.resolved } | Where-Object { $_ })[0]
    [pscustomobject]@{ schemaVersion = 1; version = $release.displayVersion; packageVersion = $release.packageVersion; assemblyFileVersion = $release.assemblyFileVersion; mcpDotnetSdk = $sdk;
        serviceCoreSha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'artifacts/app/Service/Studio5000.Core.dll')).Hash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repo 'artifacts/app/application.json') -Encoding utf8
} finally { Pop-Location }
