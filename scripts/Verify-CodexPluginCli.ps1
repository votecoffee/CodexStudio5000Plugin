param([string]$ResultsDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/test-results/codex-cli-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
if (!(Get-Command codex -ErrorAction SilentlyContinue)) { throw 'Codex CLI with plugin commands is required for this optional check.' }
. (Join-Path $PSScriptRoot 'CodexIntegration.ps1')
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
$fixture = Join-Path $ResultsDirectory 'generated app with spaces'
$profile = Join-Path $ResultsDirectory 'isolated Codex profile'
$repo = Split-Path -Parent $PSScriptRoot
$checks = [Collections.Generic.List[string]]::new()
$previousHome = $env:CODEX_HOME
function Check([bool]$Condition, [string]$Description) {
    if (!$Condition) { throw $Description }
    $checks.Add($Description); Write-Output "PASS: $Description"
}
function Plugin {
    $inventory = Invoke-RockwellCodex @('plugin','list','--json')
    $found = @($inventory.installed | Where-Object pluginId -CEQ 'rockwell-mcp-server@rockwell-mcp-server-local')
    if ($found.Count -ne 1) { throw 'Expected exactly one fixture plugin in the isolated profile.' }
    return $found[0]
}
function FixtureVersion([string]$Version, [string]$Marker) {
    foreach ($name in @('plugin.json','.codex-plugin/plugin.json')) {
        $path = Join-Path $fixture $name
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $manifest.version = $Version
        Write-RockwellIntegrationJson $path $manifest
    }
    Add-Content -LiteralPath (Join-Path $fixture 'skills/rockwell-engineering/SKILL.md') -Value $Marker
}
try {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts') -Force | Out-Null
    New-Item -ItemType Directory -Path $profile -Force | Out-Null
    foreach ($name in @('plugin.json','.mcp.json','mcp.json','LICENSE','.codex-plugin','skills')) {
        Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $fixture -Recurse
    }
    foreach ($name in @('Start-PackagedMcp.ps1','Assert-ApplicationsStopped.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixture 'scripts')
    }
    $env:CODEX_HOME = $profile
    FixtureVersion '0.0.1' 'Generated initial CLI fixture.'
    Set-RockwellCodexIntegration -Root $fixture -Route Standalone | Out-Null
    $initial = Plugin
    Check ($initial.enabled -eq $true -and $initial.version -eq '0.0.1') 'Real Codex CLI installs the standalone fixture into an isolated profile'
    $initialSource = $initial.source.path
    $initialGuide = Join-Path $initialSource 'skills/rockwell-engineering/SKILL.md'
    $initialHash = (Get-FileHash -LiteralPath $initialGuide).Hash

    # Only this generated profile is edited. A native plugin add must retain
    # independent server policy as well as the already enabled plugin choice.
    $configPath = Join-Path $profile 'config.toml'
    Add-Content -LiteralPath $configPath -Value @'

[plugins."rockwell-mcp-server@rockwell-mcp-server-local".mcp_servers.rockwell-mcp-server]
enabled = false
'@
    $configBefore = [IO.File]::ReadAllText($configPath)
    FixtureVersion '0.0.2' 'Generated enabled CLI upgrade marker.'
    $result = Set-RockwellCodexIntegration -Root $fixture
    $updated = Plugin
    Check ($updated.enabled -eq $true -and $updated.version -eq '0.0.2' -and !$result.pluginRefreshRequired) 'Reinstallation refreshes the real enabled plugin to the new package version'
    $cache = Join-Path $profile 'plugins/cache/rockwell-mcp-server-local/rockwell-mcp-server/0.0.2'
    $cachedGuide = Join-Path $cache 'skills/rockwell-engineering/SKILL.md'
    Check ((Test-Path -LiteralPath $cachedGuide) -and [IO.File]::ReadAllText($cachedGuide).Contains('Generated enabled CLI upgrade marker.')) 'The actual Codex cache contains the updated standalone workflow text'
    Check ((Get-FileHash -LiteralPath $initialGuide).Hash -eq $initialHash) 'The previous source package remains unchanged after a real CLI upgrade'
    Check ([IO.File]::ReadAllText($configPath).Replace("`r`n", "`n").Trim() -ceq $configBefore.Replace("`r`n", "`n").Trim()) 'Native package refresh preserves existing plugin and per-server configuration apart from newline normalization'
    Check ((Test-Path -LiteralPath (Join-Path $cache 'LICENSE')) -and
        (Get-ChildItem -LiteralPath $cache -File -Recurse).Count -eq 17) 'The real plugin cache contains the MIT license and only allowlisted package files'

    $currentConfig = [IO.File]::ReadAllText($configPath)
    $pattern = '(?m)^(\[plugins\."rockwell-mcp-server@rockwell-mcp-server-local"\]\r?\n)enabled = true\r?$'
    if ([regex]::Matches($currentConfig, $pattern).Count -ne 1) { throw 'Unexpected isolated plugin configuration shape.' }
    $disabledConfig = [regex]::Replace($currentConfig, $pattern, '${1}enabled = false')
    [IO.File]::WriteAllText($configPath, $disabledConfig, [Text.UTF8Encoding]::new($false))
    Check ((Plugin).enabled -eq $false) 'Generated disabled-plugin state is observed by the real CLI'
    $cacheHash = (Get-FileHash -LiteralPath $cachedGuide).Hash
    FixtureVersion '0.0.3' 'Generated disabled CLI upgrade marker.'
    $result = Set-RockwellCodexIntegration -Root $fixture
    $disabled = Plugin
    Check ($disabled.enabled -eq $false -and $disabled.version -eq '0.0.2' -and $result.pluginRefreshRequired) 'Disabled plugin stays disabled and reports that the staged update needs host refresh'
    $catalog = Get-Content -LiteralPath (Join-Path $fixture '.local/codex-marketplace/.agents/plugins/marketplace.json') -Raw | ConvertFrom-Json
    $publishedPackage = Join-Path (Join-Path $fixture '.local/codex-marketplace') $catalog.plugins[0].source.path
    $published = Get-Content -LiteralPath (Join-Path $publishedPackage 'plugin.json') -Raw | ConvertFrom-Json
    Check ($published.version -eq '0.0.3' -and [IO.File]::ReadAllText((Join-Path $publishedPackage 'skills/rockwell-engineering/SKILL.md')).Contains('Generated disabled CLI upgrade marker.')) 'The latest disabled-plugin package is published for the next host refresh'
    Check ([IO.File]::ReadAllText($configPath) -ceq $disabledConfig -and (Get-FileHash -LiteralPath $cachedGuide).Hash -eq $cacheHash) 'Disabled upgrades preserve exact configuration and current cache bytes'
    [pscustomobject]@{ passed = $checks.Count; checks = $checks; normalCodexProfileModified = $false; appStarted = $false; nativeConnectionAttempted = $false } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.json') -Encoding utf8
    Write-Output "$($checks.Count) real Codex CLI checks passed: $ResultsDirectory"
} finally { $env:CODEX_HOME = $previousHome }
