param([Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedManifestSha256, [switch]$Background)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root 'candidate.json'
if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ine $ExpectedManifestSha256) { throw 'Candidate manifest differs from the reviewed hash.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.profile -cne 'local-project' -or $manifest.state -cne 'prepared' -or $manifest.nativeAcceptance -cne 'pending') { throw 'Not a reviewed prepared native-local candidate.' }
function Test-CandidateTree([string]$Directory) {
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Candidate reparse points are refused.' }
        $relative = $item.FullName.Substring($root.TrimEnd('\').Length + 1).Replace('\','/')
        if ($relative -in @('.local','exports')) { continue } # Dedicated mutable runtime roots.
        if ($item.PSIsContainer) { Test-CandidateTree $item.FullName }
        elseif ($relative -ne 'candidate.json' -and $manifest.files.PSObject.Properties.Name -cnotcontains $relative) { throw "Unreviewed candidate payload: $relative" }
    }
}
Test-CandidateTree $root
foreach ($file in $manifest.files.PSObject.Properties) {
    $path = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $file.Name))
    if (!$path.StartsWith([IO.Path]::GetFullPath($root).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Candidate payload path escapes its workspace.' }
    for ($part = $path; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Candidate reparse points are refused.' }
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.Value) { throw "Candidate bytes changed: $($file.Name)" }
}
if (Test-Path -LiteralPath (Join-Path $root '.local/management-runtime.json')) { throw 'This candidate uses the direct app-owned service, not an optional shared host.' }
$options = (Get-Content -LiteralPath (Join-Path $root 'service.local.json') -Raw | ConvertFrom-Json).Studio5000
if ($options.NativeExecutionProfile -cne 'local-project' -or $options.EnableNativeGuiLaunch -or $options.EnableControllerOperations -or
    $options.EnableSdkServiceRecovery -or $options.AutoStartSdkService -or $options.AllowAnyControllerPath -or $options.AllowAnySourcePath -or $options.AllowAnyOutputPath -or
    @($options.ControllerTargets).Count -ne 0 -or $options.CipBridgePath) { throw 'Candidate native-local boundaries changed.' }
$keys = @('ROCKWELL_RESOURCE_REGISTRY','ROCKWELL_MANAGED_SERVICE','ROCKWELL_RESOURCE_PROFILE','ROCKWELL_NATIVE_EXECUTION_PROFILE','ROCKWELL_NATIVE_LIFECYCLE','ROCKWELL_NATIVE_SESSION','STUDIO5000_PLUGIN_ROOT')
$prior = @{}
try {
    foreach ($key in $keys) { $prior[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, $null) }
    $env:ROCKWELL_RESOURCE_REGISTRY = Join-Path $root '.local/resource-registry'
    & (Join-Path $PSScriptRoot 'Start-Service.ps1') -Background:$Background
} finally { foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $prior[$key]) } }
