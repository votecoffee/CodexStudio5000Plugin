param([string]$BridgePath, [string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$ResultsDirectory) { $ResultsDirectory = Join-Path $repo ('artifacts/test-results/cip-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
if (!$BridgePath) {
    $publishDirectory = Join-Path $ResultsDirectory 'worker'
    dotnet publish (Join-Path $repo 'src/Studio5000.CipBridge/Studio5000.CipBridge.csproj') -c Release -o $publishDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'CIP worker publish failed.' }
    $BridgePath = Join-Path $publishDirectory 'Studio5000.CipBridge.exe'
}
$fixture = Join-Path $repo 'examples/WatchDemo.L5X'
$generated = & $BridgePath --self-test $fixture | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or !$generated.success -or $generated.controllerConnected -or $generated.leafReads -ne 6) { throw 'Generated worker/native-load self-test failed.' }
$generated | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'cip-self-test.json') -Encoding utf8
$protocolChecks = @()
foreach ($scenario in @('unsupported-plan', 'changed-selection', 'startup-only')) {
    $workDirectory = Join-Path $ResultsDirectory $scenario
    New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null
    $startup = @{
        workDirectory = $workDirectory; sourcePath = $fixture; mode = 'Online'; backend = 'LibPlcTag'
        cipTarget = @{ gateway = '192.0.2.1'; route = '1,0'; timeoutMs = 100 }
        allowedTags = @(@{ key = 'enabled'; tagName = 'Enabled'; programName = 'WatchProgram' })
    }
    if ($scenario -eq 'unsupported-plan') { $startup.allowedTags += @{ key = 'text'; tagName = 'Message'; programName = 'WatchProgram' } }
    $start = [Diagnostics.ProcessStartInfo]::new($BridgePath)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $worker = [Diagnostics.Process]::Start($start)
    try {
        $stderr = $worker.StandardError.ReadToEndAsync()
        $worker.StandardInput.WriteLine(($startup | ConvertTo-Json -Depth 12 -Compress)); $worker.StandardInput.Flush()
        $line = $worker.StandardOutput.ReadLineAsync()
        if (!$line.Wait(10000)) { throw 'CIP startup timed out.' }
        $reply = $line.Result | ConvertFrom-Json
        if ($scenario -eq 'unsupported-plan') {
            if ($reply.success -or $reply.error -notlike '*supported scalar*' -or (Test-Path -LiteralPath (Join-Path $workDirectory 'metadata.l5x'))) { throw 'Unsupported complete plan was not rejected before startup.' }
        } else {
            if (!$reply.success) { throw 'Valid metadata-only startup failed.' }
            if ($scenario -eq 'changed-selection') {
                $worker.StandardInput.WriteLine('{"tags":[{"key":"enabled","tagName":"Enabled","programName":"OtherProgram"}]}'); $worker.StandardInput.Flush()
                $line = $worker.StandardOutput.ReadLineAsync()
                if (!$line.Wait(10000)) { throw 'CIP scope rejection timed out.' }
                $reply = $line.Result | ConvertFrom-Json
                if ($reply.success -or $reply.error -notlike '*Selections changed*') { throw 'Changed selection was not rejected before reading.' }
            }
        }
        $worker.StandardInput.Close()
        if (!$worker.WaitForExit(10000)) { throw 'CIP worker did not stop at EOF.' }
        $expectedExit = if ($scenario -eq 'startup-only') { 0 } else { 1 }
        if ($worker.ExitCode -ne $expectedExit) { throw "Unexpected worker exit for $scenario." }
        $protocolChecks += @{ scenario = $scenario; passed = $true; controllerConnected = $false; exitCode = $worker.ExitCode }
        $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $workDirectory 'stderr.log') -Encoding utf8
    } finally { if (!$worker.HasExited) { $worker.Kill($true); $worker.WaitForExit() }; $worker.Dispose() }
}
$protocolChecks | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'cip-protocol.json') -Encoding utf8
Write-Output "CIP offline checks passed: $ResultsDirectory"
