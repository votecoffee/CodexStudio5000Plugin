[CmdletBinding()]
param([ValidateSet('Standalone','None')][string]$Route = 'Standalone', [switch]$PlanOnly)
$ErrorActionPreference = 'Stop'
if (!$PlanOnly -and (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) '.local/maintenance-hold.json'))) { throw 'RockwellMCPServer is on operator maintenance hold. Plugin installation/configuration requires explicit human release.' }
. (Join-Path $PSScriptRoot 'CodexIntegration.ps1')
Set-RockwellCodexIntegration -Root (Split-Path -Parent $PSScriptRoot) -Route $Route -PlanOnly:$PlanOnly
if (!$PlanOnly -and $Route -eq 'Standalone') {
    $repo = Split-Path -Parent $PSScriptRoot
    $buildFile = Join-Path $repo 'artifacts/app/application.json'
    if (Test-Path -LiteralPath $buildFile) {
        $build = Get-Content -LiteralPath $buildFile -Raw | ConvertFrom-Json
        if ((Get-FileHash -LiteralPath (Join-Path $repo 'artifacts/app/Service/Studio5000.Core.dll')).Hash -ne $build.serviceCoreSha256) { throw 'Published application bytes differ from the build manifest.' }
        $plugins = Invoke-RockwellCodex @('plugin','list','--json')
        $owned = @($plugins.installed | Where-Object { $_.installed -and $_.name -ceq 'rockwell-mcp-server' -and (Test-RockwellOwnedStandalone $_ $repo) })
        if ($owned.Count -ne 1 -or !$owned[0].pluginId) { throw 'Canonical installed plugin identity is unavailable.' }
        Write-RockwellIntegrationJson (Join-Path $repo '.local/management-installation.json') @{
            schemaVersion = 1; appVersion = $build.version; pluginId = $owned[0].pluginId;
            pluginVersion = $owned[0].version; mcpDotnetSdk = $build.mcpDotnetSdk
        }
    }
}
