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
    $png = Join-Path $repo 'artifacts/test-results/desktop.png'
    $process = Start-Process -FilePath $gui -ArgumentList @('--self-test', ('"' + $png + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $png)) { throw 'Desktop render test failed.' }
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
