param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
$checks = 0
foreach ($case in @(
    @('26.0101.000001','26.101.1','26.101.0.1'),
    @('26.1011.172201','26.1011.172201','26.1011.1722.1'),
    @('26.1231.235959','26.1231.235959','26.1231.2359.59'),
    @('00.0229.090005','0.229.90005','0.229.900.5'))) {
    $identity = ConvertTo-RockwellReleaseIdentity $case[0]
    if ($identity.packageVersion -cne $case[1] -or $identity.assemblyFileVersion -cne $case[2]) { throw 'Version mapping differs.' }
    if ((Get-RockwellVersionKey $identity.displayVersion) -cne (Get-RockwellVersionKey $identity.packageVersion)) { throw 'Semantic identity differs.' }
    if (@($identity.assemblyFileVersion.Split('.') | Where-Object { [int]$_ -gt 65534 }).Count) { throw 'Assembly field overflow.' }
    $checks++
}
foreach ($invalid in @('26.0230.123456','26.0101.240000','26.0101.126060','26.101.1','26.0101.000001junk')) {
    $rejected = $false
    try { ConvertTo-RockwellReleaseIdentity $invalid | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw "Invalid timestamp accepted: $invalid" }
    $checks++
}
& (Join-Path $PSScriptRoot 'Stamp-Release.ps1') -Check | Out-Null
Write-Output "$checks timestamp boundary/semantic checks and current metadata verification passed. No version was stamped."
