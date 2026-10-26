#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination,
    [ValidateRange(1,48)][int]$MaximumMetadataClients=8,
    [ValidateRange(1,8)][int]$MaximumStartingClients=2,
    [ValidateRange(1,16)][int]$MaximumForwardedCalls=4,
    [ValidateSet('ensure-pinned-offline','existing-genuine-only')][string]$ServiceMode='ensure-pinned-offline')
$ErrorActionPreference='Stop'
if ($MaximumStartingClients -gt $MaximumMetadataClients) { throw 'Startup ramp cannot exceed metadata capacity.' }
$Destination=[IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $Destination) { throw 'Policy output already exists; preserve it and use a fresh candidate.' }
$value=[ordered]@{
    schemaVersion=1
    maximumMetadataClients=$MaximumMetadataClients
    maximumStartingClients=$MaximumStartingClients
    maximumForwardedCalls=$MaximumForwardedCalls
    perClientCommitReservationBytes=[long]268435456
    maximumTransportCommitReservationBytes=([long]($MaximumMetadataClients+$MaximumStartingClients)*268435456)
    minimumAvailableCommitBytes=[long]2147483648
    maximumProcessPrivateBytes=[long]134217728
    maximumInputFrameBytes=2097152
    maximumHttpResponseBytes=8388608
    serviceMode=$ServiceMode
}
$text=($value | ConvertTo-Json -Compress)+([char]10)
$bytes=[Text.UTF8Encoding]::new($false).GetBytes($text)
$stream=[IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
[ordered]@{path=$Destination; sha256=(Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant();
    maximumMetadataClients=$MaximumMetadataClients; maximumStartingClients=$MaximumStartingClients;
    executionSettingsChanged=$false; backendExecuted=$false} | ConvertTo-Json
