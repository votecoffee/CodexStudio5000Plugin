param([string]$ResultsDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/test-results/codex-integration-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CodexIntegration.ps1')
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$script:commands = [Collections.Generic.List[string]]::new()
$previousHome = $env:CODEX_HOME
$env:CODEX_HOME = Join-Path $ResultsDirectory 'isolated Codex profile'
function Check([bool]$Condition, [string]$Description) {
    if (!$Condition) { throw $Description }
    $checks.Add($Description); Write-Output "PASS: $Description"
}
function codex { throw 'The real Codex command must not run in fixture checks.' }
function Invoke-RockwellCodex([string[]]$Arguments) {
    $script:commands.Add('codex ' + ($Arguments -join ' '))
    if ($Arguments[1] -eq 'list') { return [pscustomobject]@{ installed = @($script:inventory) } }
    return [pscustomobject]@{ ok = $true }
}
function Invoke-RockwellBroker([object]$Runtime, [string[]]$Arguments) {
    $script:commands.Add('broker ' + ($Arguments -join ' '))
    if ($Arguments[0] -eq '--version') { return '0.11.1' }
    if ($Arguments[2] -eq 'register') {
        $manifest = Get-Content -LiteralPath $Arguments[3] -Raw | ConvertFrom-Json
        Write-RockwellIntegrationJson (Join-Path $Runtime.state 'services/rockwell-mcp-server.json') $manifest
        return '{}'
    }
    throw 'Unexpected fixture broker command.'
}
function Case([string]$Name) {
    $script:caseRoot = Join-Path $ResultsDirectory $Name
    New-Item -ItemType Directory -Path (Join-Path $caseRoot 'scripts') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assert-ApplicationsStopped.ps1') -Destination (Join-Path $caseRoot 'scripts')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-PackagedMcp.ps1') -Destination (Join-Path $caseRoot 'scripts')
    $repo = Split-Path -Parent $PSScriptRoot
    foreach ($name in @('.codex-plugin','skills')) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $caseRoot -Recurse }
    foreach ($name in @('plugin.json','.mcp.json','mcp.json')) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $caseRoot }
    $plugin = Get-Content -LiteralPath (Join-Path $caseRoot 'plugin.json') -Raw | ConvertFrom-Json
    $plugin.version = 'fixture-1'; Write-RockwellIntegrationJson (Join-Path $caseRoot 'plugin.json') $plugin
    $script:inventory = @(); $script:commands.Clear()
    $script:brokerRoot = Join-Path $caseRoot 'broker with spaces'
    $script:brokerSource = Join-Path $brokerRoot 'plugins/universal-tools'
    $script:brokerState = Join-Path $brokerRoot 'private state'
    New-Item -ItemType Directory -Path $brokerSource -Force | Out-Null
    New-Item -ItemType Directory -Path $brokerState -Force | Out-Null
    $python = Join-Path $brokerRoot 'fixture-python.exe'
    [IO.File]::WriteAllText($python, 'generated inert fixture')
    Write-RockwellIntegrationJson (Join-Path $brokerSource 'runtime.json') @{ python = $python; state = $brokerState }
    Write-RockwellIntegrationJson (Join-Path $brokerState 'settings.json') @{}
}
function Universal([bool]$Enabled = $true, [string]$Version = '0.11.1') {
    [pscustomobject]@{ pluginId = 'universal-tools@universal-tools-local'; name = 'universal-tools'; installed = $true; enabled = $Enabled; version = $Version;
        source = [pscustomobject]@{ source = 'local'; path = $brokerSource } }
}
function Receipt { Get-Content -LiteralPath (Get-ChildItem -LiteralPath (Join-Path $caseRoot '.local/codex-integration') -File | Select-Object -First 1).FullName -Raw | ConvertFrom-Json }
function Reject([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $rejected $Message
}
try {
    Case 'absent'
    $plan = Set-RockwellCodexIntegration -Root $caseRoot -PlanOnly
    Check ($plan.route -eq 'Standalone' -and !(Test-Path -LiteralPath (Join-Path $caseRoot '.local'))) 'Planning an absent-broker fallback performs no installation or local-state writes'
    Set-RockwellCodexIntegration -Root $caseRoot | Out-Null
    Check (($commands | Where-Object { $_ -like 'codex plugin add rockwell*' }).Count -eq 1 -and (Receipt).route -eq 'Standalone') 'Fresh installation without Universal Tools installs the standalone plugin with skills'
    $bundle = New-RockwellStandalonePackage $caseRoot
    $files = @(Get-ChildItem -LiteralPath $bundle.package -Recurse -File)
    Check ($files.Count -eq 11 -and !($files | Where-Object { $_.FullName.Substring($bundle.package.Length) -match '[\\/](artifacts|\.local)[\\/]' })) 'Standalone package contains only manifests, three guides, two references and a runtime launcher/pointer'
    Check ((New-RockwellStandalonePackage $caseRoot).package -eq $bundle.package) 'Identical standalone content reuses its immutable package'

    foreach ($mode in @('disabled','incompatible','missing-runtime','acceptance-pending')) {
        Case $mode
        $script:inventory = @(Universal)
        if ($mode -eq 'disabled') { $inventory[0].enabled = $false }
        if ($mode -eq 'incompatible') { $inventory[0].version = '0.8.0' }
        if ($mode -eq 'missing-runtime') { Remove-Item -LiteralPath (Join-Path $brokerSource 'runtime.json') }
        $plan = Set-RockwellCodexIntegration -Root $caseRoot -PlanOnly
        Check ($plan.route -eq 'Standalone' -and !($commands | Where-Object { $_ -like '* register *' -or $_ -like 'codex plugin add *' })) ("Auto uses standalone when broker is $mode and does not probe native targets")
    }
    Case 'accepted'
    $script:inventory = @(Universal)
    Set-RockwellCodexIntegration -Root $caseRoot -Route Broker -AcceptBrokerRoute | Out-Null
    $registration = Join-Path $brokerState 'services/rockwell-mcp-server.json'
    $manifest = Get-Content -LiteralPath $registration -Raw | ConvertFrom-Json
    Check ((Receipt).route -eq 'Broker' -and $manifest.allow_writes -eq $false -and @($manifest.allowed_write_tools).Count -eq 0 -and
        $manifest.session_mode -eq 'shared' -and $manifest.capability_catalog.tool -eq 'rockwell_broker_capabilities') 'Accepted broker selection registers the app with read-only defaults, shared service routing and normalized metadata'
    Check (!($commands | Where-Object { $_ -like 'codex plugin add *' })) 'Broker selection skips the standalone plugin installation'
    $manifest.allow_writes = $true; $manifest.allowed_write_tools = @('studio_extensions_apply')
    Write-RockwellIntegrationJson $registration $manifest
    $hash = (Get-FileHash -LiteralPath $registration).Hash
    $commands.Clear()
    Set-RockwellCodexIntegration -Root $caseRoot | Out-Null
    Check ((Get-FileHash -LiteralPath $registration).Hash -eq $hash -and !($commands | Where-Object { $_ -like '* register *' })) 'Reinstallation preserves the selected broker route and exact administrator write policy without replacing registration'
    $inventory[0].version = '0.11.2'
    Reject { Set-RockwellCodexIntegration -Root $caseRoot } 'Changed broker plugin version requires reassessment instead of silently adopting a different integration'
    $inventory[0].version = '0.11.1'
    $runtimePath = Join-Path $brokerSource 'runtime.json'
    $runtimePointer = Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json
    $originalState = $runtimePointer.state
    $runtimePointer.state = Join-Path $caseRoot 'another broker state'
    Write-RockwellIntegrationJson (Join-Path $runtimePointer.state 'settings.json') @{}
    Write-RockwellIntegrationJson $runtimePath $runtimePointer
    Reject { Set-RockwellCodexIntegration -Root $caseRoot } 'A changed broker state cannot reuse acceptance for the previous route'
    $runtimePointer.state = $originalState; Write-RockwellIntegrationJson $runtimePath $runtimePointer
    $inventory[0].enabled = $false
    Reject { Set-RockwellCodexIntegration -Root $caseRoot } 'Disabled broker on an existing broker route fails without silently switching to standalone'
    Check (!($commands | Where-Object { $_ -like 'codex plugin add *' })) 'Broker failure never triggers a cross-route installation or write retry'
    $inventory[0].enabled = $true
    Write-RockwellIntegrationJson (Join-Path $caseRoot 'plugin.json') @{ version = 'fixture-2' }
    Reject { Set-RockwellCodexIntegration -Root $caseRoot } 'Changed app version requires renewed broker workflow acceptance'
    Set-RockwellCodexIntegration -Root $caseRoot -Route Broker -AcceptBrokerRoute | Out-Null
    Reject { Set-RockwellCodexIntegration -Root $caseRoot -Route Standalone } 'Changing a broker route requires explicit reconciliation acknowledgment'
    Set-RockwellCodexIntegration -Root $caseRoot -Route Standalone -AcceptBrokerRoute | Out-Null
    Check ((Receipt).route -eq 'Standalone' -and (Get-FileHash -LiteralPath $registration).Hash -eq $hash) 'Explicit standalone restoration retains the broker registration and its recovery/policy evidence'

    Case 'foreign'
    $script:inventory = @(Universal)
    $foreign = New-RockwellBrokerManifest $caseRoot; $foreign.transport.cwd = 'C:\DifferentApplication'
    $registration = Join-Path $brokerState 'services/rockwell-mcp-server.json'
    Write-RockwellIntegrationJson $registration $foreign
    $hash = (Get-FileHash -LiteralPath $registration).Hash
    Reject { Set-RockwellCodexIntegration -Root $caseRoot -Route Broker -AcceptBrokerRoute } 'A conflicting broker registration is rejected instead of adopted or overwritten'
    Check ((Get-FileHash -LiteralPath $registration).Hash -eq $hash) 'Foreign registration bytes remain unchanged'
    Reject { Set-RockwellCodexIntegration -Root $caseRoot -Route Broker -BrokerRoot $ResultsDirectory -AcceptBrokerRoute } 'Broker source/profile mismatch fails before administrative registration'

    Case 'existing-disabled-standalone'
    $script:inventory = @((Universal), [pscustomobject]@{ name = 'rockwell-mcp-server'; installed = $true; enabled = $false })
    $plan = Set-RockwellCodexIntegration -Root $caseRoot -AcceptBrokerRoute
    Check ($plan.route -eq 'Standalone' -and !($commands | Where-Object { $_ -notlike '* list *' })) 'Auto preserves an existing disabled standalone choice even when Universal Tools is ready'
    Set-RockwellCodexIntegration -Root $caseRoot -Route None | Out-Null
    $commands.Clear()
    Check ((Set-RockwellCodexIntegration -Root $caseRoot).route -eq 'None' -and $commands.Count -eq 0) 'App-only installation stays independent and preserves its choice on later auto installs'
    [pscustomobject]@{ passed = $checks.Count; checks = $checks; realCodexModified = $false; nativeConnectionAttempted = $false } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.json') -Encoding utf8
    Write-Output "$($checks.Count) Codex integration fixture checks passed: $ResultsDirectory"
} finally { $env:CODEX_HOME = $previousHome }
