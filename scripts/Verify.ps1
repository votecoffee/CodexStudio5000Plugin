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
} finally { Pop-Location }
