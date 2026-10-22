param([string]$ResultsDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/test-results/stop-intent-'+[guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Stop-Service.ps1'),[ref]$tokens,[ref]$parseErrors)
if ($parseErrors) { throw $parseErrors }
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Publish-RockwellStopIntent'},$true)
. ([scriptblock]::Create($definition.Extent.Text)) # Trusted local function only; no stop-script body runs.
New-Item -ItemType Directory -Path $ResultsDirectory -Force|Out-Null
$checks=0
foreach ($code in @(32,33,1175,1176,1177)) {
    $target=Join-Path $ResultsDirectory "$code.target.json"; $temporary=Join-Path $ResultsDirectory "$code.tmp.json"
    [IO.File]::WriteAllText($target,'old');[IO.File]::WriteAllText($temporary,'same retained request')
    $script:attempts=0;$script:code=$code;$rejected=$false
    try {
        Publish-RockwellStopIntent $temporary $target {
            param($source,$destination)
            $script:attempts++
            if ($script:attempts -le 2) { throw [IO.IOException]::new('Synthetic replacement failure',(-2147024896+$script:code)) }
            [IO.File]::Replace($source,$destination,[NullString]::Value)
        }
    } catch { $rejected=$true; $lastError=$_.Exception.Message }
    if ($code -in @(32,33,1175)) {
        if ($rejected -or $script:attempts -lt 3 -or $script:attempts -gt 7 -or [IO.File]::ReadAllText($target) -cne 'same retained request') { throw "Safe metadata retry failed: code=$code attempts=$script:attempts rejected=$rejected error=$lastError" }
    } elseif (!$rejected -or $script:attempts -ne 1 -or [IO.File]::ReadAllText($target) -cne 'old') { throw 'Ambiguous replacement was retried' }
    $checks++
}
Write-Output "$checks metadata-only stop-intent retry checks passed. No service/controller operation ran."
