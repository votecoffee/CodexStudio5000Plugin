[CmdletBinding()]
param([ValidateSet('Standalone','None')][string]$Route = 'Standalone', [switch]$PlanOnly)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CodexIntegration.ps1')
Set-RockwellCodexIntegration -Root (Split-Path -Parent $PSScriptRoot) -Route $Route -PlanOnly:$PlanOnly
