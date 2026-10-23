# Installation and root commands

RockwellMCPServer installs as a standalone service, GUI and MCP adapter with its own Codex plugin/skills. The optional shared framework and separate Windows management app live in [CodexMCPBroker](https://github.com/votecoffee/CodexMCPBroker); a universal plugin is not part of installation. Reviewed app pins remain authoritative.

## Commands

Run from the checkout or invoke the quoted absolute CMD path from another directory:

```powershell
.\Install.cmd --help
.\Run.cmd --help
.\Install.cmd
.\Run.cmd -ProjectPath "C:\Projects\project file.L5X"
```

Both CMDs accept `--help`, `-Help`, `-?` and `/?` as their first argument. Help is handled in the wrapper before PowerShell, so it starts no service/GUI, creates no local state and changes no Codex configuration.

`Install.cmd` forwards its arguments to `scripts/Install.ps1`. Supported options include `-WithSdk`, `-WithCip`, `-SkipBuild`, `-NoShortcut`, `-CodexIntegration Standalone|None` and explicit `-ReconnectIdleMcpClients`. Normal installation builds/configures the app, publishes extensions and installs its app-owned plugin. `None` skips Codex integration; it does not make the rest of installation read-only. Existing settings and disabled plugin choices are preserved according to the [integration contract](CODEX_INTEGRATION.md).

`Run.cmd` forwards `-ProjectPath` and `-ShowErrorDialog` to `scripts/Start.ps1`, starting the installed service and desktop. Quote paths containing spaces. Wrappers use their own directory, select Windows PowerShell's module environment and return the exact PowerShell exit code, including failure.

Finish jobs/polling, close clients and complete the app's authenticated stop workflow before replacing binaries. Do not reinstall/restart a working deployment for documentation checks. Native SDK/CIP setup, controller permissions, staged settings, native pilot acceptance and chat reconnect are separate steps. [Native-local candidate preparation](NATIVE_LOCAL_PILOT.md) publishes elsewhere and never activates the normal installation.

## Disposable verification

```powershell
python -m unittest discover -s tests/python -p test_root_cmd.py
```

Windows fixtures copy the actual wrappers into a generated Unicode path containing spaces. File-only PowerShell stubs verify exact argument forwarding, quoted Unicode values, module environment selection and failure exit 37. Copies of the actual install/start scripts verify all help spellings without entering their bodies. The tests select a disposable `CODEX_HOME` and never invoke a real Codex install or engineering executable.

For deeper installer behavior, `scripts/Verify-CodexIntegration.ps1` and `Verify-InstallerSettings.ps1` use mocked Codex/build/application commands and temporary marker files. Real CLI/cache/runtime acceptance is a separate authorized check. Never use a live repaired configuration for a fixture; see [configuration byte publication](CONFIG_PUBLICATION.md).
