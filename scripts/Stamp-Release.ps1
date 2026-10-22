param([string]$DisplayVersion, [switch]$Check)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
if (!$Check) {
    # The only clock read used to create an app version. Build/start never stamp.
    $instant = [DateTimeOffset]::UtcNow
    if (!$DisplayVersion) {
        try { $zone = [TimeZoneInfo]::FindSystemTimeZoneById('America/New_York') }
        catch { $zone = [TimeZoneInfo]::FindSystemTimeZoneById('Eastern Standard Time') }
        $DisplayVersion = [TimeZoneInfo]::ConvertTime($instant,$zone).ToString('yy.MMdd.HHmmss',[Globalization.CultureInfo]::InvariantCulture)
    }
    $identity = ConvertTo-RockwellReleaseIdentity $DisplayVersion
    $checkpoint = $instant.ToString('o')
    $existingPath = Join-Path $repo 'release-version.json'
    if (Test-Path -LiteralPath $existingPath) {
        $existing = Get-Content -LiteralPath $existingPath -Raw | ConvertFrom-Json
        if ($existing.displayVersion -ceq $identity.displayVersion) { $checkpoint = ([DateTimeOffset]$existing.checkpointUtc).ToUniversalTime().ToString('o') }
    }
    $release = [ordered]@{ schemaVersion=1; displayVersion=$identity.displayVersion; packageVersion=$identity.packageVersion;
        assemblyFileVersion=$identity.assemblyFileVersion; timeZone=$identity.timeZone; checkpointUtc=$checkpoint }
    $utf8 = [Text.UTF8Encoding]::new($false)
    $json = ($release | ConvertTo-Json) + [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $repo 'release-version.json'),$json,$utf8)
    [IO.File]::WriteAllText((Join-Path $repo 'framework/src/rockwell_framework/release-version.json'),$json,$utf8)
    $props = @"
<Project>
  <PropertyGroup>
    <RockwellDisplayVersion>$($identity.displayVersion)</RockwellDisplayVersion>
    <RockwellPackageVersion>$($identity.packageVersion)</RockwellPackageVersion>
    <RockwellAssemblyVersion>$($identity.assemblyFileVersion)</RockwellAssemblyVersion>
  </PropertyGroup>
</Project>
"@
    [IO.File]::WriteAllText((Join-Path $repo 'ReleaseVersion.props'),$props+[Environment]::NewLine,$utf8)
    foreach ($name in @('plugin.json','.codex-plugin/plugin.json')) {
        $path = Join-Path $repo $name; $text = [IO.File]::ReadAllText($path)
        $text = [regex]::Replace($text,'("version"\s*:\s*")[^"]+("\s*,)',[Text.RegularExpressions.MatchEvaluator]{param($match) $match.Groups[1].Value+$identity.packageVersion+$match.Groups[2].Value})
        [IO.File]::WriteAllText($path,$text,$utf8)
    }
    $pyproject = Join-Path $repo 'framework/pyproject.toml'
    $text = [regex]::Replace([IO.File]::ReadAllText($pyproject),'(?m)^version = "[^"]+"','version = "'+$identity.packageVersion+'"')
    [IO.File]::WriteAllText($pyproject,$text,$utf8)
    $readme = Join-Path $repo 'README.md'
    if (Test-Path -LiteralPath $readme) {
        $text = [IO.File]::ReadAllText($readme)
        $updated = [regex]::Replace($text,'(currently \*\*)[0-9]{2}\.[0-9]{4}\.[0-9]{6}(\*\*)',[Text.RegularExpressions.MatchEvaluator]{param($match) $match.Groups[1].Value+$identity.displayVersion+$match.Groups[2].Value})
        if ($updated -cne $text) { [IO.File]::WriteAllText($readme,$updated,$utf8) }
    }
}
$release = Get-RockwellRelease $repo
foreach ($name in @('plugin.json','.codex-plugin/plugin.json')) {
    $manifest = Get-Content -LiteralPath (Join-Path $repo $name) -Raw | ConvertFrom-Json
    if ((Get-RockwellVersionKey $manifest.version) -cne $release.packageVersion) { throw "Manifest identity differs: $name" }
}
$python = Get-Content -LiteralPath (Join-Path $repo 'framework/pyproject.toml') -Raw
if ($python -notmatch ('(?m)^version = "'+[regex]::Escape($release.packageVersion)+'"\s*$')) { throw 'Python package version differs.' }
$embedded = Get-Content -LiteralPath (Join-Path $repo 'framework/src/rockwell_framework/release-version.json') -Raw | ConvertFrom-Json
if ($embedded.displayVersion -cne $release.displayVersion -or $embedded.packageVersion -cne $release.packageVersion) { throw 'Embedded release identity differs.' }
$props = [xml](Get-Content -LiteralPath (Join-Path $repo 'ReleaseVersion.props') -Raw)
if ($props.Project.PropertyGroup.RockwellDisplayVersion -cne $release.displayVersion -or $props.Project.PropertyGroup.RockwellPackageVersion -cne $release.packageVersion -or $props.Project.PropertyGroup.RockwellAssemblyVersion -cne $release.assemblyFileVersion) { throw 'MSBuild release identity differs.' }
$release
