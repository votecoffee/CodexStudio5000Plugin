param([Alias('SkipBuild')][switch]$SkipUnitTests, [switch]$WithCip)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$fixtureRoot = Join-Path $repo ('artifacts/isolated-validation/' + [guid]::NewGuid().ToString('N'))
$previousRoot = $env:STUDIO5000_PLUGIN_ROOT
$process = $null
Push-Location -LiteralPath $repo
try {
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'scripts') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'examples') -Destination $fixtureRoot -Recurse
    Copy-Item -LiteralPath (Join-Path $repo 'scripts/Start-Mcp.ps1') -Destination (Join-Path $fixtureRoot 'scripts')
    if (!$SkipUnitTests) {
        dotnet test tests/Studio5000.Tests/Studio5000.Tests.csproj -c Release --nologo --logger 'trx;LogFileName=core.trx' --results-directory (Join-Path $fixtureRoot 'artifacts/test-results')
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    }
    foreach ($name in @('Service', 'Mcp', 'Desktop', 'ExtensionWorker')) {
        dotnet publish "src/Studio5000.$name/Studio5000.$name.csproj" -c Release -o (Join-Path $fixtureRoot "artifacts/app/$name") --nologo
        if ($LASTEXITCODE -ne 0) { throw "$name publish failed." }
    }
    if ($WithCip) {
        $cipOutput = Join-Path $fixtureRoot 'artifacts/app/CipBridge'
        dotnet publish src/Studio5000.CipBridge/Studio5000.CipBridge.csproj -c Release -o $cipOutput --nologo
        if ($LASTEXITCODE -ne 0) { throw 'CIP worker publish failed.' }
        & (Join-Path $PSScriptRoot 'Verify-CipOffline.ps1') -BridgePath (Join-Path $cipOutput 'Studio5000.CipBridge.exe') -ResultsDirectory (Join-Path $fixtureRoot 'artifacts/test-results')
    }
    & (Join-Path $PSScriptRoot 'Build-Extensions.ps1') -PackageRoot (Join-Path $fixtureRoot 'extensions')
    foreach ($fixtureVersion in @('1', '2')) {
        $fixtureOutput = Join-Path $fixtureRoot "fixture-build-$fixtureVersion"
        $fixtureV2 = if ($fixtureVersion -eq '2') { 'true' } else { 'false' }
        dotnet publish tests/Studio5000.ExtensionFixture/Studio5000.ExtensionFixture.csproj -c Release -p:FixtureV2=$fixtureV2 -o $fixtureOutput --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Reload fixture build failed.' }
        & (Join-Path $PSScriptRoot 'Publish-Extension.ps1') -Id 'fixture' -Version $fixtureVersion -PublishedDirectory $fixtureOutput -EntryAssembly 'Studio5000.ExtensionFixture.dll' -EntryType 'Studio5000.ExtensionFixture.Fixture' -PackageRoot (Join-Path $fixtureRoot 'extensions')
    }
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
    $config = Get-Content -LiteralPath (Join-Path $repo 'service.example.json') -Raw | ConvertFrom-Json
    $config.ServiceUrl = "http://127.0.0.1:$port"
    $config.Studio5000.EnableControllerOperations = $false
    $config.Studio5000.ControllerTargets = @()
    $config.Studio5000.NativeBridgePath = $null
    $config.Studio5000.CipBridgePath = $null
    $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'service.local.json') -Encoding utf8
    $env:STUDIO5000_PLUGIN_ROOT = $fixtureRoot
    $process = Start-Process -FilePath (Join-Path $fixtureRoot 'artifacts/app/Service/Studio5000.Service.exe') -WorkingDirectory $fixtureRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $fixtureRoot 'service.log') -RedirectStandardError (Join-Path $fixtureRoot 'service-error.log')
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($process.HasExited) { throw 'Isolated service exited; inspect service-error.log.' }
        try { $ready = (Invoke-RestMethod -Uri ($config.ServiceUrl + '/health') -TimeoutSec 1).status -eq 'ok' } catch { }
        if ($ready) { break }
        Start-Sleep -Milliseconds 100
    }
    if (!$ready) { throw 'Isolated service did not start.' }
    dotnet run --project (Join-Path $repo 'tests/Studio5000.Integration/Studio5000.Integration.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Isolated REST/MCP checks failed.' }
    $gui = Join-Path $fixtureRoot 'artifacts/app/Desktop/Studio5000.Desktop.exe'
    foreach ($view in @('tag-references', 'references', 'reference-parameters', 'comparison', 'ladder', 'browser', 'trends', 'extensions', 'viewdesigner', 'watches', 'watches-values', 'online-edit', 'main')) {
        $flag = if ($view -eq 'main') { '--self-test' } else { "--self-test-$view" }
        $png = Join-Path $fixtureRoot "artifacts/test-results/desktop-$view.png"
        $guiProcess = Start-Process -FilePath $gui -ArgumentList @($flag, ('"' + $png + '"')) -WindowStyle Hidden -PassThru -Wait
        if ($guiProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $png)) { throw "$view GUI render failed." }
    }
    Write-Output "Isolated verification passed: $fixtureRoot"
} finally {
    if ($null -ne $process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    $env:STUDIO5000_PLUGIN_ROOT = $previousRoot
    Pop-Location
}
