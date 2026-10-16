$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$env:STUDIO5000_PLUGIN_ROOT = $repo
Push-Location -LiteralPath $repo
try {
    dotnet test tests/Studio5000.Tests/Studio5000.Tests.csproj -c Release --nologo --logger 'trx;LogFileName=core.trx' --results-directory artifacts/test-results
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    dotnet run --project tests/Studio5000.Integration/Studio5000.Integration.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'REST/MCP integration failed. Start the shared service first.' }
    $gui = Join-Path $repo 'artifacts/app/Desktop/Studio5000.Desktop.exe'
    $extensionsPng = Join-Path $repo 'artifacts/test-results/desktop-extensions.png'
    $extensionsProcess = Start-Process -FilePath $gui -ArgumentList @('--self-test-extensions', ('"' + $extensionsPng + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($extensionsProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $extensionsPng)) { throw 'Extensions GUI render test failed.' }
    $viewPng = Join-Path $repo 'artifacts/test-results/desktop-viewdesigner.png'
    $viewProcess = Start-Process -FilePath $gui -ArgumentList @('--self-test-viewdesigner', ('"' + $viewPng + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($viewProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $viewPng)) { throw 'View Designer workspace render test failed.' }
    foreach ($watchView in @('browser', 'trends', 'watches', 'watches-values')) {
        $watchPng = Join-Path $repo ("artifacts/test-results/desktop-$watchView.png")
        $watchProcess = Start-Process -FilePath $gui -ArgumentList @("--self-test-$watchView", ('"' + $watchPng + '"')) -WindowStyle Hidden -PassThru -Wait
        if ($watchProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $watchPng)) { throw 'Desktop watch-list render test failed.' }
    }
    $png = Join-Path $repo 'artifacts/test-results/desktop.png'
    $process = Start-Process -FilePath $gui -ArgumentList @('--self-test', ('"' + $png + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $png)) { throw 'Desktop render test failed.' }
    $onlinePng = Join-Path $repo 'artifacts/test-results/desktop-online-edit.png'
    $onlineProcess = Start-Process -FilePath $gui -ArgumentList @('--self-test-online-edit', ('"' + $onlinePng + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($onlineProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $onlinePng)) { throw 'Online edit planner render test failed.' }
    $connection = Get-Content -LiteralPath (Join-Path $repo '.local/connection.json') -Raw | ConvertFrom-Json
    $headers = @{ Authorization = 'Bearer ' + (Get-Content -LiteralPath $connection.tokenFile -Raw).Trim() }
    $sdkStatus = Invoke-RestMethod -Uri ($connection.url + '/api/sdk/capabilities') -Headers $headers
    if ($sdkStatus.nativeBridgeConfigured) {
        $sdkPng = Join-Path $repo 'artifacts/test-results/desktop-SDK.png'
        $sdkProcess = Start-Process -FilePath $gui -ArgumentList @('--self-test-sdk', ('"' + $sdkPng + '"')) -WindowStyle Hidden -PassThru -Wait
        if ($sdkProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $sdkPng)) { throw 'Desktop SDK render test failed.' }
    }
    $schemaDir = 'C:\Program Files (x86)\Rockwell Software\RSView Enterprise\Schemas'
    if (Test-Path -LiteralPath $schemaDir) {
        foreach ($edition in @('ME','SE')) {
            $env:STUDIO5000_GUI_FIXTURE = "Demo$edition.xml"
            $graphicsPng = Join-Path $repo "artifacts/test-results/desktop-$edition.png"
            $graphicsProcess = Start-Process -FilePath $gui -ArgumentList @('--self-test', ('"' + $graphicsPng + '"')) -WindowStyle Hidden -PassThru -Wait
            if ($graphicsProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $graphicsPng)) { throw "Desktop $edition render test failed." }
        }
        Remove-Item Env:\STUDIO5000_GUI_FIXTURE
    }
} finally { Pop-Location }
