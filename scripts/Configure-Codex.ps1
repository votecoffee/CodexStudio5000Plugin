param([ValidateSet('Auto','Standalone','Broker','None')][string]$Route = 'Auto',
    [string]$BrokerRoot, [switch]$AcceptBrokerRoute, [switch]$PlanOnly)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CodexIntegration.ps1')
Set-RockwellCodexIntegration -Root (Split-Path -Parent $PSScriptRoot) -Route $Route -BrokerRoot $BrokerRoot -AcceptBrokerRoute:$AcceptBrokerRoute -PlanOnly:$PlanOnly
