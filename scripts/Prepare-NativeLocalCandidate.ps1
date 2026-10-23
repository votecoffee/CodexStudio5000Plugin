param([string]$Destination, [ValidateRange(1024,65535)][int]$Port = 5488)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
$release = Get-RockwellRelease $repo
if (!$Destination) { $Destination = Join-Path $repo ('artifacts/native-local-candidates/' + $release.packageVersion) }
if (![IO.Path]::IsPathRooted($Destination)) { $Destination = [IO.Path]::GetFullPath((Join-Path $repo $Destination)) }
$Destination = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $Destination) { throw 'Candidate destinations are immutable. Use a new empty destination; preserve prior evidence.' }
New-Item -ItemType Directory -Path $Destination | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-CandidateJson([string]$Path, [object]$Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 32) + [Environment]::NewLine, $utf8) }
try {
    & (Join-Path $PSScriptRoot 'Stamp-Release.ps1') -Check | Out-Null
    # Publish only into this candidate. Never call the normal installer/Build.ps1,
    # select normal extension pointers, replace app binaries or touch Codex config.
    foreach ($role in @('Service','Mcp','StdioHost','Desktop','ExtensionWorker','NativeBridge')) {
        $directory = if ($role -eq 'StdioHost') { 'Mcp' } else { $role }
        dotnet publish (Join-Path $repo "src/Studio5000.$role/Studio5000.$role.csproj") -c Release --no-restore -o (Join-Path $Destination "artifacts/app/$directory") --nologo
        if ($LASTEXITCODE) { throw "Candidate $role compilation failed; incomplete output is retained." }
    }
    $extensionRoot = Join-Path $Destination 'extensions'
    & (Join-Path $PSScriptRoot 'Build-Extensions.ps1') -Version $release.packageVersion -PackageRoot $extensionRoot -ParallelWorkers 1
    foreach ($id in @('logix','factorytalk','viewdesigner','watches')) { Write-CandidateJson (Join-Path $extensionRoot "$id/active.json") @{ version=$release.packageVersion } }
    foreach ($name in @('scripts','fixtures','exports')) { New-Item -ItemType Directory -Path (Join-Path $Destination $name) -Force | Out-Null }
    foreach ($name in @('Start-Service.ps1','Stop-Service.ps1','Start-Mcp.ps1','Start-NativeLocalCandidate.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $Destination "scripts/$name") }
    foreach ($name in @('LICENSE','release-version.json')) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination (Join-Path $Destination $name) }
    Copy-Item -LiteralPath (Join-Path $repo 'examples/NativeLocalProgram.L5X') -Destination (Join-Path $Destination 'fixtures/NativeLocalProgram.L5X')
    $raw = & (Join-Path $Destination 'artifacts/app/Service/Studio5000.Service.exe') --native-local-settings $Destination
    if ($LASTEXITCODE) { throw 'Pure Core configuration generation failed; no service was started.' }
    $options = ($raw -join [Environment]::NewLine) | ConvertFrom-Json
    Write-CandidateJson (Join-Path $Destination 'service.local.json') @{ ServiceUrl="http://127.0.0.1:$Port"; Studio5000=$options }
    Copy-Item -LiteralPath (Join-Path $repo 'docs/NATIVE_LOCAL_PILOT.md') -Destination (Join-Path $Destination 'PILOT.md')
    $files = [ordered]@{}
    Get-ChildItem -LiteralPath $Destination -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Destination.TrimEnd('\').Length + 1).Replace('\','/')
        $files[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $commit = (& git -C $repo rev-parse HEAD).Trim()
    Write-CandidateJson (Join-Path $Destination 'candidate.json') ([ordered]@{ schemaVersion=1; state='prepared'; profile='local-project'; version=$release.displayVersion; packageVersion=$release.packageVersion;
        sourceCommit=$commit; sourceDirty=([bool](& git -C $repo status --porcelain)); nativeAcceptance='pending'; vendorCodeExecuted=$false;
        scmControlEnabled=$false; controllerEnabled=$false; nativeGuiEnabled=$false; files=$files })
    Write-Output "Prepared only; not installed or started: $Destination"
    Write-Output "Manifest SHA-256: $((Get-FileHash -LiteralPath (Join-Path $Destination 'candidate.json')).Hash.ToLowerInvariant())"
} catch {
    Write-CandidateJson (Join-Path $Destination 'candidate-failure.json') @{ state='incomplete'; nativeAcceptance='pending'; error=$_.Exception.Message }
    throw
}
