param([string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$ResultsDirectory) { $ResultsDirectory = Join-Path $repo ('artifacts/test-results/native-local-' + [guid]::NewGuid().ToString('N')) }
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
if (Test-Path -LiteralPath $ResultsDirectory) { throw 'Use a new disposable native-profile fixture directory.' }
New-Item -ItemType Directory -Path $ResultsDirectory | Out-Null
$runner = Join-Path $ResultsDirectory 'runner'
dotnet publish (Join-Path $repo 'tests/Studio5000.ResourceInterop/Studio5000.ResourceInterop.csproj') -c Release --no-restore -o $runner --nologo
if ($LASTEXITCODE) { throw 'Harmless adapter fixture compilation failed.' }
foreach ($mode in @('native-owner', 'native-owner-uncertain')) {
    $fixture = Join-Path $ResultsDirectory $mode
    New-Item -ItemType Directory -Path $fixture | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture '.resource-interop-fixture'), 'Harmless exact native-adapter contract fixture')
    & (Join-Path $runner 'Studio5000.ResourceInterop.exe') $mode $fixture
    if ($LASTEXITCODE) { throw "Harmless native adapter contract failed: $mode; preserve its private records." }
    $result = Get-Content -LiteralPath (Join-Path $fixture 'native-adapter-result.json') -Raw | ConvertFrom-Json
    if ($result.scmAccessed -or $result.vendorCodeExecuted) { throw 'Unexpected native/SCM fixture behavior.' }
    Write-Output "$mode passed: $($result.status); shutdown refused=$($result.shutdownRefused)"
}
Write-Output "Two real contained harmless worker cases passed. Native SDK/controller acceptance remains separate: $ResultsDirectory"
