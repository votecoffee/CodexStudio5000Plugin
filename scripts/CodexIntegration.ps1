# Shared by the installer and the explicit integration command. No dot-source side effects.
function Invoke-RockwellCodex([string[]]$Arguments) {
    $raw = & codex @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Codex plugin command failed; existing integration was retained.' }
    return ($raw -join "`n") | ConvertFrom-Json
}
function Write-RockwellIntegrationJson([string]$Path, [object]$Value) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
        for ($attempt = 0; ; $attempt++) {
            try {
                if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
                else { [IO.File]::Move($temporary, $Path) }
                break
            } catch [IO.IOException] {
                if ($attempt -ge 7) { throw }
                Start-Sleep -Milliseconds 25
            }
        }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}
function New-RockwellStandalonePackage([string]$Root) {
    $marketplace = Join-Path $Root '.local/codex-marketplace'
    $staging = Join-Path $marketplace ('staging/' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $staging '.codex-plugin') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $staging 'scripts') -Force | Out-Null
    foreach ($name in @('plugin.json', '.codex-plugin/plugin.json', '.mcp.json', 'mcp.json', 'LICENSE', 'release-version.json')) {
        Copy-Item -LiteralPath (Join-Path $Root $name) -Destination (Join-Path $staging $name)
    }
    # Explicit public-content allowlist. Never copy the checkout, .local, artifacts or projects.
    foreach ($name in @('rockwell-engineering','logix-online-edits','factorytalk-studio')) {
        $source = Join-Path $Root "skills/$name"
        foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File -Filter '*.md') {
            $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/')
            $destination = Join-Path $staging "skills/$name/$relative"
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
    Copy-Item -LiteralPath (Join-Path $Root 'scripts/Start-PackagedMcp.ps1') -Destination (Join-Path $staging 'scripts/Start-Mcp.ps1')
    Write-RockwellIntegrationJson (Join-Path $staging 'runtime.json') @{ schemaVersion = 1; root = $Root }
    $snapshot = (Get-ChildItem -LiteralPath $staging -Recurse -File | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($staging.Length).Replace('\', '/') + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n"
    $digest = [Security.Cryptography.SHA256]::Create()
    try { $contentId = ([BitConverter]::ToString($digest.ComputeHash([Text.Encoding]::UTF8.GetBytes($snapshot)))).Replace('-', '').ToLowerInvariant() }
    finally { $digest.Dispose() }
    $packageName = 'rockwell-mcp-server-' + $contentId.Substring(0, 20)
    $destination = Join-Path $marketplace "packages/$packageName"
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    # All computed move/delete targets must stay within this generated package workspace.
    foreach ($path in @($staging, $destination)) {
        if (![IO.Path]::GetFullPath($path).StartsWith([IO.Path]::GetFullPath($marketplace).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid generated package path.' }
    }
    if (Test-Path -LiteralPath $destination) {
        $existingSnapshot = (Get-ChildItem -LiteralPath $destination -Recurse -File | Sort-Object FullName | ForEach-Object {
            $_.FullName.Substring($destination.Length).Replace('\', '/') + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }) -join "`n"
        if ($snapshot -cne $existingSnapshot) { throw 'An existing immutable plugin package has different bytes.' }
        Remove-Item -LiteralPath $staging -Recurse
    } else { [IO.Directory]::Move($staging, $destination) }
    Write-RockwellIntegrationJson (Join-Path $marketplace '.agents/plugins/marketplace.json') @{
        name = 'rockwell-mcp-server-local'; interface = @{ displayName = 'RockwellMCPServer Local' }
        plugins = @(@{ name = 'rockwell-mcp-server'; source = @{ source = 'local'; path = "./packages/$packageName" };
            policy = @{ installation = 'AVAILABLE'; authentication = 'ON_INSTALL' }; category = 'Developer Tools' })
    }
    return [pscustomobject]@{ marketplace = $marketplace; package = $destination; sha256 = $contentId }
}
function Test-RockwellOwnedStandalone([object]$Plugin, [string]$Root) {
    if ($Plugin.pluginId -cne 'rockwell-mcp-server@rockwell-mcp-server-local' -or $Plugin.source.source -cne 'local' -or
        !$Plugin.source.path -or !$Plugin.marketplaceSource.source) { return $false }
    $marketplace = [IO.Path]::GetFullPath((Join-Path $Root '.local/codex-marketplace')).TrimEnd('\', '/')
    $marketplaceSource = [IO.Path]::GetFullPath(($Plugin.marketplaceSource.source -replace '^\\\\\?\\', '')).TrimEnd('\', '/')
    $packageSource = [IO.Path]::GetFullPath(($Plugin.source.path -replace '^\\\\\?\\', ''))
    return $marketplaceSource -ieq $marketplace -and $packageSource.StartsWith(($marketplace + '\packages\'), [StringComparison]::OrdinalIgnoreCase)
}
# Retired integration records are evidence, never a routing policy. Preserve exact
# bytes before replacing one; external registrations are outside this installer.
function Save-RockwellLegacyIntegrationReceipt([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $digest = [Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($digest.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $digest.Dispose() }
    $archive = Join-Path (Split-Path -Parent $Path) ('legacy/' + [IO.Path]::GetFileNameWithoutExtension($Path) + '-' + $hash + '.json')
    New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
    if (Test-Path -LiteralPath $archive) {
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $hash) { throw 'Legacy integration archive has different bytes.' }
    } else { [IO.File]::WriteAllBytes($archive, $bytes) }
}
function Set-RockwellCodexIntegration {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root, [ValidateSet('Standalone','None')][string]$Route = 'Standalone', [switch]$PlanOnly)
    $Root = [IO.Path]::GetFullPath($Root)
    $codexHome = if ($env:CODEX_HOME) { [IO.Path]::GetFullPath($env:CODEX_HOME) } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex' }
    $digest = [Security.Cryptography.SHA256]::Create()
    try { $profileId = ([BitConverter]::ToString($digest.ComputeHash([Text.Encoding]::UTF8.GetBytes($codexHome.ToLowerInvariant())))).Replace('-', '').Substring(0, 20).ToLowerInvariant() }
    finally { $digest.Dispose() }
    $receiptPath = Join-Path $Root ".local/codex-integration/$profileId.json"
    $prior = $null
    if (Test-Path -LiteralPath $receiptPath) {
        $prior = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        if ($prior.schemaVersion -ne 1 -or $prior.profile -ine $codexHome -or $prior.root -ine $Root -or $prior.route -notin @('Standalone','Broker','None')) {
            throw 'Invalid integration receipt; inspect local installation state before configuring the plugin.'
        }
    }
    $legacy = $prior -and $prior.route -eq 'Broker'
    if ($Route -eq 'None') {
        if (!$PlanOnly) {
            if ($legacy) { Save-RockwellLegacyIntegrationReceipt $receiptPath }
            Write-RockwellIntegrationJson $receiptPath @{ schemaVersion = 1; root = $Root; profile = $codexHome; route = 'None' }
        }
        return [pscustomobject]@{ route = 'None'; ready = $true; reason = 'Codex integration skipped; existing plugins and external registrations retained.' }
    }
    if (!(Get-Command codex -ErrorAction SilentlyContinue)) { throw 'Codex CLI with plugin commands is required. Use -Route None for app-only installation, or run Configure-Codex.ps1 later.' }
    $plugins = Invoke-RockwellCodex @('plugin','list','--json')
    if ($null -eq $plugins.installed) { throw 'Unrecognized Codex plugin inventory; no integration was changed.' }
    $standalone = @($plugins.installed | Where-Object { $_.name -ceq 'rockwell-mcp-server' -and $_.installed -eq $true })
    if ($standalone.Count -gt 1) { throw 'Ambiguous Rockwell plugin inventory; select the intended Codex profile before installation.' }
    $reason = 'Standalone Codex plugin selected.'
    $ownedStandalone = $standalone.Count -eq 1 -and (Test-RockwellOwnedStandalone $standalone[0] $Root)
    $refreshRequired = $ownedStandalone -and $standalone[0].enabled -ne $true
    if (!$PlanOnly) {
        # A first installation only publishes a new immutable package. Binary
        # rebuilds are guarded by Install.ps1. Enabled-plugin refreshes and
        # migrating retired integration records still require an idle app.
        if ($legacy -or ($ownedStandalone -and !$refreshRequired)) { & (Join-Path $Root 'scripts/Assert-ApplicationsStopped.ps1') }
        if ($legacy) { Save-RockwellLegacyIntegrationReceipt $receiptPath }
        if (!$standalone.Count -or $ownedStandalone) {
            $bundle = New-RockwellStandalonePackage $Root
            if (!$standalone.Count) { Invoke-RockwellCodex @('plugin','marketplace','add',$bundle.marketplace,'--json') | Out-Null }
            # Codex plugin add enables its target. Refresh only an already enabled,
            # app-owned installation; publish disabled updates for host refresh.
            if (!$refreshRequired) { Invoke-RockwellCodex @('plugin','add','rockwell-mcp-server@rockwell-mcp-server-local','--json') | Out-Null }
        }
        Write-RockwellIntegrationJson $receiptPath @{ schemaVersion = 1; root = $Root; profile = $codexHome; route = 'Standalone' }
    }
    if ($legacy) { $reason += ' Retired integration receipt is archived on apply; external registrations and their recovery state are retained without invocation.' }
    if ($refreshRequired) {
        if ($PlanOnly) { $reason += ' An updated package would be staged while the plugin stays disabled; no files have changed.' }
        else { $reason += ' Updated package staged; plugin remains disabled. Refresh it through Codex when needed.' }
    }
    elseif ($standalone.Count -and !$ownedStandalone) { $reason += ' Existing plugin belongs to another installation; its package was retained.' }
    return [pscustomobject]@{ route = 'Standalone'; ready = $true; pluginRefreshRequired = $refreshRequired;
        reason = $reason + ' Standalone skills and direct MCP remain available. Start a new chat after plugin installation.' }
}
