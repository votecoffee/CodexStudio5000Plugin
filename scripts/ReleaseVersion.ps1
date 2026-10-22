# Pure version helpers. Loading this file never stamps or installs a release.
function ConvertTo-RockwellReleaseIdentity([string]$DisplayVersion) {
    if ($DisplayVersion -cnotmatch '\A[0-9]{2}\.[0-9]{4}\.[0-9]{6}\z') { throw 'Use YY.MMDD.HHmmss with all timestamp fields padded.' }
    $parts = $DisplayVersion.Split('.')
    $year = 2000 + [int]$parts[0]; $month = [int]$parts[1].Substring(0,2); $day = [int]$parts[1].Substring(2,2)
    $hour = [int]$parts[2].Substring(0,2); $minute = [int]$parts[2].Substring(2,2); $second = [int]$parts[2].Substring(4,2)
    $local = [DateTime]::new($year,$month,$day,$hour,$minute,$second,[DateTimeKind]::Unspecified)
    [pscustomobject]@{ displayVersion=$DisplayVersion; packageVersion=('{0}.{1}.{2}' -f [int]$parts[0],[int]$parts[1],[int]$parts[2]);
        assemblyFileVersion=('{0}.{1}.{2}.{3}' -f [int]$parts[0],[int]$parts[1],($hour*100+$minute),$second);
        timeZone='America/New_York'; localTime=$local.ToString('yyyy-MM-ddTHH:mm:ss',[Globalization.CultureInfo]::InvariantCulture) }
}
function Get-RockwellVersionKey([string]$Version) {
    if ($Version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw 'Expected a numeric three-part application/package version.' }
    ($Version.Split('.') | ForEach-Object { [uint32]$_ }) -join '.'
}
function Get-RockwellRelease([string]$Root) {
    $value = Get-Content -LiteralPath (Join-Path $Root 'release-version.json') -Raw | ConvertFrom-Json
    $expected = ConvertTo-RockwellReleaseIdentity $value.displayVersion
    if ($value.schemaVersion -ne 1 -or $value.timeZone -cne 'America/New_York' -or
        $value.packageVersion -cne $expected.packageVersion -or $value.assemblyFileVersion -cne $expected.assemblyFileVersion) { throw 'Release identity fields disagree; run Stamp-Release.ps1 once, then verify.' }
    $value
}
