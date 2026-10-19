# Shared by the installer and the explicit integration command. No dot-source side effects.
function Invoke-RockwellCodex([string[]]$Arguments) {
    $raw = & codex @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Codex plugin command failed; existing integration was retained.' }
    return ($raw -join "`n") | ConvertFrom-Json
}
function Invoke-RockwellBroker([object]$Runtime, [string[]]$Arguments) {
    $raw = & $Runtime.python -m universal_tools @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Universal Tools administrative command failed; no route was selected.' }
    return ($raw -join "`n")
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
    foreach ($name in @('plugin.json', '.codex-plugin/plugin.json', '.mcp.json', 'mcp.json')) {
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
function Get-RockwellBrokerRuntime([object]$Plugin, [string]$BrokerRoot) {
    if (!$Plugin -or $Plugin.installed -ne $true -or $Plugin.enabled -ne $true) { throw 'Universal Tools is absent or disabled in this Codex profile.' }
    if ([version]$Plugin.version -lt [version]'0.11.1') { throw 'Universal Tools 0.11.1 or newer is required for assessed shared sessions and workflow guidance.' }
    if ($Plugin.source.source -ne 'local' -or !$Plugin.source.path) { throw 'A supported local Universal Tools installation is required.' }
    $source = [IO.Path]::GetFullPath($Plugin.source.path).TrimEnd('\', '/')
    if (!$BrokerRoot) { $BrokerRoot = Split-Path -Parent (Split-Path -Parent $source) }
    $BrokerRoot = [IO.Path]::GetFullPath($BrokerRoot).TrimEnd('\', '/')
    if ([IO.Path]::GetFullPath((Join-Path $BrokerRoot 'plugins/universal-tools')).TrimEnd('\', '/') -ine $source) {
        throw 'BrokerRoot does not match the enabled plugin source; select the intended Codex profile.'
    }
    $runtimeFile = Join-Path $source 'runtime.json'
    if (!(Test-Path -LiteralPath $runtimeFile -PathType Leaf)) { throw 'Universal Tools runtime pointer is missing; complete broker setup first.' }
    $runtime = Get-Content -LiteralPath $runtimeFile -Raw | ConvertFrom-Json
    if (![IO.Path]::IsPathRooted($runtime.python) -or !(Test-Path -LiteralPath $runtime.python -PathType Leaf) -or
        ![IO.Path]::IsPathRooted($runtime.state) -or !(Test-Path -LiteralPath (Join-Path $runtime.state 'settings.json') -PathType Leaf)) {
        throw 'Universal Tools runtime or initialized state is unavailable.'
    }
    $version = (Invoke-RockwellBroker $runtime @('--version')).Trim()
    if ([version]$version -lt [version]'0.11.1') { throw 'The broker runtime must be version 0.11.1 or newer.' }
    return [pscustomobject]@{ python = $runtime.python; state = $runtime.state; root = $BrokerRoot; version = $version }
}
function New-RockwellBrokerManifest([string]$Root) {
    return [ordered]@{
        schema_version = 1; id = 'rockwell-mcp-server'; display_name = 'RockwellMCPServer'
        description = 'App-owned Rockwell PLC/HMI engineering service, live extensions and versioned workflow guidance.'
        transport = [ordered]@{ type = 'stdio'; command = (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe');
            args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $Root 'scripts/Start-Mcp.ps1')); cwd = $Root; env_vars = @() }
        enabled = $true; allow_writes = $false; allowed_write_tools = @(); read_only_tools = @()
        timeout_seconds = 240; max_argument_bytes = 8388608
        session_mode = 'shared'; lease_seconds = 900; resource_id = 'rockwell-mcp-server'
        capability_catalog = [ordered]@{ schema_version = 1; tool = 'rockwell_broker_capabilities'; arguments = @{} }
    }
}
function Assert-RockwellBrokerRegistration([object]$Existing, [object]$Desired) {
    # Never overwrite a registration, broaden its policy or adopt a foreign endpoint.
    if ($Existing.id -cne $Desired.id -or $Existing.enabled -ne $true -or $Existing.session_mode -cne 'shared' -or
        $Existing.transport.type -cne 'stdio' -or $Existing.transport.command -ine $Desired.transport.command -or
        $Existing.transport.cwd -ine $Desired.transport.cwd -or
        (@($Existing.transport.args) -join "`n") -cne (@($Desired.transport.args) -join "`n") -or
        @($Existing.transport.env_vars).Count -ne 0 -or $Existing.capability_catalog.tool -cne 'rockwell_broker_capabilities' -or
        $Existing.capability_catalog.schema_version -ne 1 -or
        @($Existing.capability_catalog.arguments.PSObject.Properties).Count -ne 0) {
        throw 'The broker registration differs or is disabled. Review it with the broker administrator; the installer will not replace it.'
    }
}
function Set-RockwellCodexIntegration {
    param([string]$Root, [ValidateSet('Auto','Standalone','Broker','None')][string]$Route = 'Auto',
        [string]$BrokerRoot, [switch]$AcceptBrokerRoute, [switch]$PlanOnly)
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
            throw 'Invalid integration receipt; inspect local installation state before selecting a route.'
        }
    }
    if ($Route -eq 'Auto' -and $prior) { $Route = $prior.route }
    if ($Route -eq 'None') {
        if (!$PlanOnly) { Write-RockwellIntegrationJson $receiptPath @{ schemaVersion = 1; root = $Root; profile = $codexHome; route = 'None' } }
        return [pscustomobject]@{ route = 'None'; ready = $true; reason = 'Codex integration skipped; existing plugins and registrations retained.' }
    }
    if (!(Get-Command codex -ErrorAction SilentlyContinue)) {
        if ($Route -ne 'Auto') { throw 'Codex CLI with plugin commands is required for this integration route.' }
        return [pscustomobject]@{ route = 'None'; ready = $false; reason = 'Codex CLI unavailable; app installation is independent. Run Configure-Codex.ps1 later.' }
    }
    $plugins = Invoke-RockwellCodex @('plugin','list','--json')
    if ($null -eq $plugins.installed) { throw 'Unrecognized Codex plugin inventory; no integration was changed.' }
    $universal = @($plugins.installed | Where-Object pluginId -CEQ 'universal-tools@universal-tools-local')
    $standalone = @($plugins.installed | Where-Object { $_.name -ceq 'rockwell-mcp-server' -and $_.installed -eq $true })
    if ($universal.Count -gt 1 -or $standalone.Count -gt 1) { throw 'Ambiguous plugin inventory; choose one profile and integration before installation.' }
    $appVersion = (Get-Content -LiteralPath (Join-Path $Root 'plugin.json') -Raw | ConvertFrom-Json).version
    $runtime = $null; $reason = 'Standalone integration explicitly selected or preserved.'
    $accepted = $AcceptBrokerRoute -or ($prior -and $prior.route -eq 'Broker' -and $prior.acceptedAppVersion -ceq $appVersion)
    if ($Route -eq 'Auto') {
        if ($standalone.Count) { $Route = 'Standalone'; $reason = 'Preserved the existing standalone plugin and its enabled/disabled choice.' }
        else {
            try {
                $runtime = Get-RockwellBrokerRuntime ($universal | Select-Object -First 1) $BrokerRoot
                if (!$accepted) { throw 'Broker workflow acceptance is pending; use explicit Broker selection after the documented fresh-chat checks.' }
                $Route = 'Broker'; $reason = 'Compatible enabled Universal Tools and accepted workflow route are available.'
            } catch { $Route = 'Standalone'; $reason = 'Standalone fallback: ' + $_.Exception.Message }
        }
    }
    if ($Route -eq 'Broker') {
        if (!$runtime) { $runtime = Get-RockwellBrokerRuntime ($universal | Select-Object -First 1) $BrokerRoot }
        if (!$accepted) { throw 'Broker workflow acceptance is required for this app version. After fresh-chat checks and reconciliation, pass -AcceptBrokerRoute.' }
        if ($prior -and $prior.route -eq 'Broker' -and !$AcceptBrokerRoute -and
            ($prior.brokerRoot -ine $runtime.root -or $prior.brokerState -ine $runtime.state -or $prior.brokerPython -ine $runtime.python -or
             $prior.acceptedBrokerVersion -cne $runtime.version -or $prior.acceptedPluginVersion -cne $universal[0].version)) {
            throw 'The accepted broker installation/version changed. Reconcile the prior route and renew acceptance before selecting the new one.'
        }
        if ($prior -and $prior.route -ne 'Broker' -and !$AcceptBrokerRoute) { throw 'Explicit route switching requires -AcceptBrokerRoute after reconciling outstanding work.' }
        $manifest = New-RockwellBrokerManifest $Root
        $registeredPath = Join-Path $runtime.state 'services/rockwell-mcp-server.json'
        $existing = Test-Path -LiteralPath $registeredPath
        if ($existing) { Assert-RockwellBrokerRegistration (Get-Content -LiteralPath $registeredPath -Raw | ConvertFrom-Json) $manifest }
        if (!$PlanOnly) {
            # Changing integrations requires an idle app; this does not stop anything.
            if (!$prior -or $prior.route -ne 'Broker') { & (Join-Path $Root 'scripts/Assert-ApplicationsStopped.ps1') }
            $manifestPath = Join-Path $Root '.local/broker/rockwell-mcp-server.service.json'
            if (!$existing) {
                Write-RockwellIntegrationJson $manifestPath $manifest
                Invoke-RockwellBroker $runtime @('--state', $runtime.state, 'register', $manifestPath) | Out-Null
                Assert-RockwellBrokerRegistration (Get-Content -LiteralPath $registeredPath -Raw | ConvertFrom-Json) $manifest
            }
            Write-RockwellIntegrationJson $receiptPath @{ schemaVersion = 1; root = $Root; profile = $codexHome; route = 'Broker'; brokerRoot = $runtime.root;
                brokerState = $runtime.state; brokerPython = $runtime.python; acceptedAppVersion = $appVersion;
                acceptedBrokerVersion = $runtime.version; acceptedPluginVersion = $universal[0].version }
        }
        return [pscustomobject]@{ route = 'Broker'; ready = $true; reason = 'Use Universal Tools; standalone package retained. Existing plugins and broker write policy preserved. Start the app service, then refresh_services.' }
    }
    if ($prior -and $prior.route -eq 'Broker' -and !$AcceptBrokerRoute) { throw 'Route changes require -AcceptBrokerRoute as acknowledgment that broker work and uncertain outcomes have been reconciled.' }
    if (!$PlanOnly) {
        if (!$prior -or $prior.route -ne 'Standalone') { & (Join-Path $Root 'scripts/Assert-ApplicationsStopped.ps1') }
        if (!$standalone.Count) {
            $bundle = New-RockwellStandalonePackage $Root
            Invoke-RockwellCodex @('plugin','marketplace','add',$bundle.marketplace,'--json') | Out-Null
            Invoke-RockwellCodex @('plugin','add','rockwell-mcp-server@rockwell-mcp-server-local','--json') | Out-Null
        }
        Write-RockwellIntegrationJson $receiptPath @{ schemaVersion = 1; root = $Root; profile = $codexHome; route = 'Standalone' }
    }
    return [pscustomobject]@{ route = 'Standalone'; ready = $true; reason = $reason + ' Standalone skills and direct MCP remain available. Start a new chat after plugin installation.' }
}
