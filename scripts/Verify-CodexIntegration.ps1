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
function Case([string]$Name) {
    $script:caseRoot = Join-Path $ResultsDirectory $Name
    New-Item -ItemType Directory -Path (Join-Path $caseRoot 'scripts') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assert-ApplicationsStopped.ps1') -Destination (Join-Path $caseRoot 'scripts')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-PackagedMcp.ps1') -Destination (Join-Path $caseRoot 'scripts')
    $repo = Split-Path -Parent $PSScriptRoot
    foreach ($name in @('.codex-plugin','skills')) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $caseRoot -Recurse }
    foreach ($name in @('plugin.json','.mcp.json','mcp.json','LICENSE')) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $caseRoot }
    $plugin = Get-Content -LiteralPath (Join-Path $caseRoot 'plugin.json') -Raw | ConvertFrom-Json
    $plugin.version = 'fixture-1'; Write-RockwellIntegrationJson (Join-Path $caseRoot 'plugin.json') $plugin
    $script:inventory = @(); $script:commands.Clear()
}
function Receipt { Get-Content -LiteralPath (Get-ChildItem -LiteralPath (Join-Path $caseRoot '.local/codex-integration') -File | Select-Object -First 1).FullName -Raw | ConvertFrom-Json }
function Reject([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $rejected $Message
}
try {
    $installerAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install.ps1'), [ref]$null, [ref]$null)
    $integrationParameter = $installerAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'CodexIntegration' }
    Check ($integrationParameter.DefaultValue.Value -ceq 'Standalone') 'Normal installation defaults to the standalone plugin'
    Case 'application-update-lock'
    foreach ($name in @('Install.ps1', 'Start-Mcp.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $caseRoot 'scripts') }
    New-Item -ItemType Directory -Path (Join-Path $caseRoot '.local') -Force | Out-Null
    $lockPath = Join-Path $caseRoot '.local/application-update.lock'
    $held = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $previousRoot = $env:STUDIO5000_PLUGIN_ROOT
    try {
        $blocked = $false; try { & (Join-Path $caseRoot 'scripts/Start-Mcp.ps1') } catch { $blocked = $_.Exception.Message -like '*installation is in progress*' }
        Check $blocked 'An active update blocks MCP reconnect before launching a bridge'
        $blocked = $false; try { & (Join-Path $caseRoot 'scripts/Install.ps1') -SkipBuild -NoShortcut } catch { $blocked = $_.Exception.Message -like '*Another RockwellMCPServer update*' }
        Check $blocked 'A second installer cannot enter an active application update'
    } finally { $held.Dispose(); $env:STUDIO5000_PLUGIN_ROOT = $previousRoot }
    $missing = $false; try { & (Join-Path $caseRoot 'scripts/Start-Mcp.ps1') } catch { $missing = $_.Exception.Message -like '*Run scripts/Build.ps1 first*' } finally { $env:STUDIO5000_PLUGIN_ROOT = $previousRoot }
    Check $missing 'An unlocked leftover update file does not block the normal launcher'
    $failed = $false; try { & (Join-Path $caseRoot 'scripts/Install.ps1') -SkipBuild -NoShortcut } catch { $failed = $_.Exception.Message -like '*Missing Service application*' }
    $probe = [IO.File]::Open($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $probe.Dispose()
    Check $failed 'A failed installation releases its update lock without starting an application'
    foreach ($name in @('Service','Mcp','Desktop','ExtensionWorker')) {
        $directory = Join-Path $caseRoot "artifacts/app/$name"; New-Item -ItemType Directory -Path $directory -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $directory "Studio5000.$name.exe"), 'generated nonexecutable placeholder')
    }
    [IO.File]::WriteAllText((Join-Path $caseRoot 'service.local.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $caseRoot 'scripts/Configure-Codex.ps1'), @'
param([string]$Route)
$root = Split-Path -Parent $PSScriptRoot
$blocked = $false
try { $probe = [IO.File]::Open((Join-Path $root '.local/application-update.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $probe.Dispose() }
catch [IO.IOException] { $blocked = $true }
if (!$blocked) { throw 'Installer did not retain its update lock through plugin configuration.' }
[IO.File]::WriteAllText((Join-Path $root '.local/guard-observed'), 'held')
'@)
    & (Join-Path $caseRoot 'scripts/Install.ps1') -SkipBuild -NoShortcut | Out-Null
    Check (Test-Path -LiteralPath (Join-Path $caseRoot '.local/guard-observed')) 'The installer retains its update lock through plugin configuration'
    $probe = [IO.File]::Open($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $probe.Dispose()
    Check $true 'A successful installation releases the lock so new MCP connections can start'
    Case 'absent'
    $plan = Set-RockwellCodexIntegration -Root $caseRoot -PlanOnly
    Check ($plan.route -eq 'Standalone' -and !(Test-Path -LiteralPath (Join-Path $caseRoot '.local'))) 'Planning standalone installation performs no installation or local-state writes'
    Set-RockwellCodexIntegration -Root $caseRoot | Out-Null
    Check (($commands | Where-Object { $_ -like 'codex plugin add rockwell*' }).Count -eq 1 -and (Receipt).route -eq 'Standalone') 'Fresh installation installs the standalone plugin with skills'
    $bundle = New-RockwellStandalonePackage $caseRoot
    $files = @(Get-ChildItem -LiteralPath $bundle.package -Recurse -File)
    Check ($files.Count -eq 10 -and !($files | Where-Object { $_.FullName.Substring($bundle.package.Length) -match '[\\/](artifacts|\.local|workflows)[\\/]' })) 'Standalone package contains only manifests, MIT license, three bootstrap skills and a runtime launcher/pointer'
    $portableMcp = Get-Content -LiteralPath (Join-Path $bundle.package 'mcp.json') -Raw | ConvertFrom-Json
    Check ($portableMcp.'$schema' -ceq 'https://agent-plugins.org/schemas/1.0.0/mcp.schema.json' -and
        $portableMcp.mcpServers.'rockwell-mcp-server'.type -ceq 'stdio') 'Portable MCP configuration declares its required schema and standalone transport'
    Check ((New-RockwellStandalonePackage $caseRoot).package -eq $bundle.package) 'Identical standalone content reuses its immutable package'
    $originalGuide = Join-Path $bundle.package 'skills/rockwell-engineering/SKILL.md'
    $originalHash = (Get-FileHash -LiteralPath $originalGuide).Hash
    $script:inventory = @([pscustomobject]@{ pluginId = 'rockwell-mcp-server@rockwell-mcp-server-local'; name = 'rockwell-mcp-server'; installed = $true; enabled = $true;
        source = [pscustomobject]@{ source = 'local'; path = $bundle.package }; marketplaceSource = [pscustomobject]@{ source = $bundle.marketplace } })
    Add-Content -LiteralPath (Join-Path $caseRoot 'skills/rockwell-engineering/SKILL.md') -Value 'Generated enabled update.'
    $commands.Clear()
    Set-RockwellCodexIntegration -Root $caseRoot | Out-Null
    $updatedBundle = New-RockwellStandalonePackage $caseRoot
    Check ($updatedBundle.package -ne $bundle.package -and @($commands | Where-Object { $_ -like 'codex plugin add *' }).Count -eq 1) 'Reinstall republishes changed standalone guidance and refreshes an enabled app-owned plugin'
    Check ((Get-FileHash -LiteralPath $originalGuide).Hash -eq $originalHash) 'Standalone upgrade retains the exact previous immutable package for rollback'
    $inventory[0].source.path = $updatedBundle.package; $inventory[0].enabled = $false
    Add-Content -LiteralPath (Join-Path $caseRoot 'skills/rockwell-engineering/SKILL.md') -Value 'Generated disabled update.'
    $commands.Clear()
    $plan = Set-RockwellCodexIntegration -Root $caseRoot
    Check ($plan.pluginRefreshRequired -and !($commands | Where-Object { $_ -like 'codex plugin add *' }) -and
        (New-RockwellStandalonePackage $caseRoot).package -ne $updatedBundle.package) 'Disabled app-owned plugins receive staged updates without invoking an enabling command'
    $inventory[0].marketplaceSource.source = '\\?\' + $updatedBundle.marketplace
    Check (Test-RockwellOwnedStandalone $inventory[0] $caseRoot) 'Owned-package detection handles Windows extended-length marketplace paths'
    $inventory[0].source.path = Join-Path $caseRoot '.local/codex-marketplace/packages-other/foreign'
    Check (!(Test-RockwellOwnedStandalone $inventory[0] $caseRoot)) 'A similarly prefixed foreign package is not adopted by the installer'

    Case 'fresh-plugin-busy-runtime'
    [IO.File]::WriteAllText((Join-Path $caseRoot 'scripts/Assert-ApplicationsStopped.ps1'), "throw 'Generated busy app runtime.'")
    Set-RockwellCodexIntegration -Root $caseRoot -Route Standalone | Out-Null
    Check (@($commands | Where-Object { $_ -like 'codex plugin add rockwell*' }).Count -eq 1) 'A first standalone plugin can be installed without stopping the independently running app'
    $busyBundle = New-RockwellStandalonePackage $caseRoot
    $script:inventory = @([pscustomobject]@{ pluginId = 'rockwell-mcp-server@rockwell-mcp-server-local'; name = 'rockwell-mcp-server'; installed = $true; enabled = $true;
        source = [pscustomobject]@{ source = 'local'; path = $busyBundle.package }; marketplaceSource = [pscustomobject]@{ source = $busyBundle.marketplace } })
    Reject { Set-RockwellCodexIntegration -Root $caseRoot -Route Standalone } 'Refreshing an enabled owned plugin still requires an idle app'

    Case 'other-plugins'
    $script:inventory = @([pscustomobject]@{ pluginId = 'universal-tools@universal-tools-local'; name = 'universal-tools'; installed = $true; enabled = $true; version = '99.0.0' })
    Set-RockwellCodexIntegration -Root $caseRoot | Out-Null
    Check ((Receipt).route -eq 'Standalone' -and @($commands | Where-Object { $_ -like 'codex plugin add rockwell*' }).Count -eq 1) 'Other plugins never select an alternate route or suppress standalone installation'
    Check (!($commands | Where-Object { $_ -notmatch '^codex plugin (list --json|marketplace add .+ --json|add rockwell-mcp-server@rockwell-mcp-server-local --json)$' })) 'Installer only lists plugins and installs its own marketplace/package'

    Case 'foreign-standalone'
    $script:inventory = @([pscustomobject]@{ name = 'rockwell-mcp-server'; installed = $true; enabled = $false })
    $plan = Set-RockwellCodexIntegration -Root $caseRoot
    Check ($plan.route -eq 'Standalone' -and !($commands | Where-Object { $_ -notlike '* list *' }) -and !(Test-Path -LiteralPath (Join-Path $caseRoot '.local/codex-marketplace'))) 'A foreign disabled standalone plugin and its package are preserved'
    $script:inventory += $inventory[0]
    Reject { Set-RockwellCodexIntegration -Root $caseRoot } 'Ambiguous Rockwell plugin installations fail before package changes'

    Case 'app-only'
    Set-RockwellCodexIntegration -Root $caseRoot -Route None -PlanOnly | Out-Null
    Check ($commands.Count -eq 0 -and !(Test-Path -LiteralPath (Join-Path $caseRoot '.local'))) 'App-only planning needs no Codex inventory and writes no state'
    Set-RockwellCodexIntegration -Root $caseRoot -Route None | Out-Null
    Check ((Receipt).route -eq 'None' -and $commands.Count -eq 0) 'Explicit app-only installation skips Codex completely'
    Set-RockwellCodexIntegration -Root $caseRoot | Out-Null
    Check ((Receipt).route -eq 'Standalone') 'A later default install explicitly selects standalone regardless of the earlier app-only receipt'

    Case 'removed-options'
    foreach ($route in @('Auto','Broker')) {
        Reject { Set-RockwellCodexIntegration -Root $caseRoot -Route $route } ("Retired $route route is rejected")
        Reject { & (Join-Path $PSScriptRoot 'Install.ps1') -CodexIntegration $route -SkipBuild -NoShortcut } ("Installer rejects retired $route selection before installing")
        Reject { & (Join-Path $PSScriptRoot 'Configure-Codex.ps1') -Route $route -PlanOnly } ("Configuration command rejects retired $route selection")
    }
    Reject { Set-RockwellCodexIntegration -Root $caseRoot -BrokerRoot $caseRoot } 'Removed broker-root parameter is rejected by integration function'
    Reject { & (Join-Path $PSScriptRoot 'Install.ps1') -AcceptBrokerRoute -SkipBuild -NoShortcut } 'Removed acceptance parameter is rejected before any installer work'
    Reject { & (Join-Path $PSScriptRoot 'Configure-Codex.ps1') -BrokerRoot $caseRoot -PlanOnly } 'Removed broker-root parameter is rejected by configuration command'
    Check ($commands.Count -eq 0 -and !(Test-Path -LiteralPath (Join-Path $caseRoot '.local'))) 'Rejected options never invoke Codex or write installation state'

    Case 'legacy-receipt'
    Set-RockwellCodexIntegration -Root $caseRoot -Route None | Out-Null
    $receiptPath = (Get-ChildItem -LiteralPath (Join-Path $caseRoot '.local/codex-integration') -File | Select-Object -First 1).FullName
    $legacy = Receipt; $legacy.route = 'Broker'
    Write-RockwellIntegrationJson $receiptPath $legacy
    $legacyHash = (Get-FileHash -LiteralPath $receiptPath).Hash
    $externalState = Join-Path $caseRoot 'external-service-registration.json'
    [IO.File]::WriteAllText($externalState, 'Generated registration and recovery evidence, not owned by this installer.')
    $externalHash = (Get-FileHash -LiteralPath $externalState).Hash
    $commands.Clear()
    $plan = Set-RockwellCodexIntegration -Root $caseRoot -PlanOnly
    Check ($plan.route -eq 'Standalone' -and (Get-FileHash -LiteralPath $receiptPath).Hash -eq $legacyHash -and !(Test-Path -LiteralPath (Join-Path $caseRoot '.local/codex-integration/legacy'))) 'A retired receipt plans standalone without invoking its old route or archiving during preview'
    [IO.File]::WriteAllText((Join-Path $caseRoot 'scripts/Assert-ApplicationsStopped.ps1'), "throw 'Generated busy app runtime.'")
    Reject { Set-RockwellCodexIntegration -Root $caseRoot } 'Migrating a retired integration receipt requires an idle app'
    Check ((Get-FileHash -LiteralPath $receiptPath).Hash -eq $legacyHash -and !($commands | Where-Object { $_ -notlike '* list *' })) 'Busy migration preserves the receipt and makes no plugin changes'
    [IO.File]::WriteAllText((Join-Path $caseRoot 'scripts/Assert-ApplicationsStopped.ps1'), '# Generated idle app runtime.')
    Set-RockwellCodexIntegration -Root $caseRoot | Out-Null
    $archive = @(Get-ChildItem -LiteralPath (Join-Path $caseRoot '.local/codex-integration/legacy') -File)
    Check ((Receipt).route -eq 'Standalone' -and $archive.Count -eq 1 -and (Get-FileHash -LiteralPath $archive[0].FullName).Hash -eq $legacyHash) 'Standalone migration archives the exact retired receipt bytes'
    Check ((Get-FileHash -LiteralPath $externalState).Hash -eq $externalHash) 'Migration preserves external registrations and recovery evidence without managing them'
    [IO.File]::WriteAllBytes($receiptPath, [IO.File]::ReadAllBytes($archive[0].FullName))
    [IO.File]::WriteAllText($archive[0].FullName, 'Generated conflicting archive fixture.')
    $commands.Clear()
    Reject { Set-RockwellCodexIntegration -Root $caseRoot } 'A conflicting legacy archive prevents migration before changing the plugin'
    Check ((Get-FileHash -LiteralPath $receiptPath).Hash -eq $legacyHash -and !($commands | Where-Object { $_ -notlike '* list *' })) 'Archive failure preserves the receipt and makes no plugin changes'
    $invalid = Receipt; $invalid.profile = Join-Path $ResultsDirectory 'wrong profile'
    Write-RockwellIntegrationJson $receiptPath $invalid
    Reject { Set-RockwellCodexIntegration -Root $caseRoot } 'A mismatched profile receipt cannot be adopted'

    Case 'missing-cli'
    function Get-Command { param([string]$Name, $ErrorAction) if ($Name -ne 'codex') { throw 'Unexpected command lookup.' } }
    try {
        Reject { Set-RockwellCodexIntegration -Root $caseRoot } 'Standalone installation reports a missing Codex CLI'
        Set-RockwellCodexIntegration -Root $caseRoot -Route None | Out-Null
        Check ((Receipt).route -eq 'None' -and $commands.Count -eq 0) 'App-only installation works without Codex installed'
    } finally { Remove-Item Function:Get-Command }
    [pscustomobject]@{ passed = $checks.Count; checks = $checks; realCodexModified = $false; nativeConnectionAttempted = $false } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.json') -Encoding utf8
    Write-Output "$($checks.Count) Codex integration fixture checks passed: $ResultsDirectory"
} finally { $env:CODEX_HOME = $previousHome }
