#Requires -Version 5.1
# Copied to the app-owned Start-Mcp.ps1; never resolves a Codex cache or checkout launcher.
$ErrorActionPreference = 'Stop'
try {
    if ($args.Count) { throw 'The stable MCP entrypoint accepts no command arguments.' }
    $pointer = Join-Path $PSScriptRoot 'launcher/current.json'
    if (![IO.File]::Exists($pointer) -or [IO.FileInfo]::new($pointer).Length -gt 65536) { throw 'Prepare a reviewed Rockwell stable backend before connecting.' }
    $selection = [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($pointer)).TrimStart([char]0xfeff) | ConvertFrom-Json
    $module = Join-Path $PSScriptRoot 'StableMcp.psm1'
    if ([IO.FileInfo]::new($module).Length -gt 131072) { throw 'Stable module exceeds its budget.' }
    $algorithm = [Security.Cryptography.SHA256]::Create(); $stream = [IO.File]::OpenRead($module)
    try { $moduleHash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
    if ($selection.module_sha256 -notmatch '^[a-f0-9]{64}$' -or $moduleHash -cne $selection.module_sha256) { throw 'Stable module bytes differ.' }
    Import-Module $module -Force -DisableNameChecking
    $backend = Get-RockwellStableSelection $PSScriptRoot
    if (Test-Path -LiteralPath (Join-Path $backend.appRoot '.local/maintenance-hold.json')) { throw 'Operator maintenance hold blocks MCP startup.' }
    $update = Join-Path $backend.appRoot '.local/application-update.lock'
    if (Test-Path -LiteralPath $update) {
        $probe = $null
        try { $probe = [IO.File]::Open($update, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite) }
        catch [IO.IOException] { throw 'Application update is in progress; reconnect when it finishes.' }
        finally { if ($probe) { $probe.Dispose() } }
    }
    $utf8 = [Text.UTF8Encoding]::new($false)
    [Console]::InputEncoding = $utf8; [Console]::OutputEncoding = $utf8; $OutputEncoding = $utf8
    $env:STUDIO5000_PLUGIN_ROOT = $backend.appRoot
    Set-Location -LiteralPath $PSScriptRoot
    & (Join-Path $backend.snapshot 'Studio5000.StdioHost.exe')
    exit $LASTEXITCODE
} catch { [Console]::Error.WriteLine("Rockwell stable launcher: $($_.Exception.Message)"); exit 1 }
