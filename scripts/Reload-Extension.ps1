param([Parameter(Mandatory)][string]$Id, [string]$Version, [string]$ExpectedGeneration)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$connection = Get-Content -LiteralPath (Join-Path $repo '.local/connection.json') -Raw | ConvertFrom-Json
$headers = @{ Authorization = 'Bearer ' + (Get-Content -LiteralPath $connection.tokenFile -Raw).Trim() }
$body = @{ extensionId = $Id }
if ($Version) { $body.version = $Version }
if ($ExpectedGeneration) { $body.expectedGeneration = $ExpectedGeneration }
Invoke-RestMethod -Uri ($connection.url + '/api/extensions/reload') -Method Post -Headers $headers -ContentType 'application/json' -Body ($body | ConvertTo-Json) |
    Select-Object Id, Version, Generation, State, PackageSha256
