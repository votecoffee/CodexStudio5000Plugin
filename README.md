# RockwellMCPServer

Shared Rockwell engineering service with MCP, REST, a desktop GUI and reloadable extensions.

Inspect and edit **Studio 5000 Logix Designer**, **FactoryTalk View Studio ME/SE** and **Studio 5000 View Designer** project exports from a Codex chat or the desktop workspace. Browse tags and UDTs, create watch lists, record values and export trends. Optional Logix Designer SDK jobs provide native project and configured controller operations.

All clients use one authenticated local service and the same operations. Source files are preserved; reviewed changes create managed revisions and new exports. Controller operations are disabled by default.

The plugin identifier is `rockwell-mcp-server`. Internal `Studio5000.*` assemblies, environment variables and existing tool names remain for compatibility.

## Table of Contents

- [Background](#background)
- [Install](#install)
- [Usage](#usage)
- [Documentation](#documentation)
- [Validation](#validation)
- [API](#api)
- [Maintainers](#maintainers)
- [Contributing](#contributing)
- [License](#license)

## Background

RockwellMCPServer gives agents and engineers a shared workspace for project inspection, controlled edits and repeatable exports. The MCP adapter and Windows desktop call the same service, so changes and watch lists are visible across clients.

| Capability | Scope |
|---|---|
| Logix project editing | Inspect and preview/apply L5X component changes; export L5X or exploded ZIP. Optional SDK conversion supports ACD files. |
| Logix SDK jobs | Ordered native calls for projects, builds, imports, typed/raw tags and other operations in the bound SDK version. Availability depends on the installed Logix version and project. |
| FactoryTalk View ME/SE | Edit existing graphics XML properties using matching installed schemas; extract tag references with source selectors, prepare import packages and compare re-exports. |
| View Designer | Edit existing properties in exported `.hmi` folders, preserve assets, prepare import ZIPs and compare re-exports. |
| Tag/UDT browser and watches | Search snapshot structures, expand members/arrays, create stopped watch lists, poll authorized tags and extract structures as JSON. |
| Trending and recording | Persist history across restarts, graph numeric/BOOL values, show quality/stale gaps and export CSV. |
| Reloadable extensions | Discover operation schemas and reload compatible modules through MCP or the GUI while the host and chat remain running. |

L5X explode/implode uses the MIT-licensed L5xploder library from `ra-logix-designer-vcs-custom-tools`. See [third-party provenance](THIRD_PARTY_NOTICES.md).

Exploded ZIPs use legacy schema 1. Newer layouts, protected AOIs and structured-text metadata that this layout cannot preserve are rejected explicitly; L5X exports remain available. See [exploded-project compatibility](docs/EXPLODED_PROJECTS.md).

## Install

### Dependencies

- Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0), including desktop and ASP.NET Core runtimes.
- Git and PowerShell for the commands below.
- For ACD/native operations: a licensed, installed Logix Designer SDK and compatible Logix Designer versions. The bridge currently binds SDK **2.1.974**.
- For FactoryTalk XML mutation/import preparation: the matching installed ME/SE graphics schemas. Native import requires FactoryTalk View Studio.
- For native View Designer import/build: Studio 5000 View Designer. Export-folder editing itself needs no native executable.

Proprietary SDK binaries, schemas and activation files are not distributed in this repository.

### Build and configure

Run in PowerShell:

```powershell
git clone https://github.com/votecoffee/RockwellMCPServer.git
cd RockwellMCPServer
.\scripts\Build.ps1
.\scripts\Initialize.ps1
.\scripts\Start-Service.ps1 -Background
.\scripts\Start-Gui.ps1
```

This starts with the generated inputs under `examples/`. Add an existing source directory with `.\scripts\Initialize.ps1 -AllowedRoot 'C:\Projects\Rockwell'`. Initialization preserves existing settings and protects local credentials/state for the current Windows user and SYSTEM.

For optional native SDK support, use `Build.ps1 -WithSdk` and `Initialize.ps1 -EnableSdk` in place of the basic build/configure commands. This does not enable controller operations.

Local settings are in ignored `service.local.json`; use [service.example.json](service.example.json) as the portable reference. `AllowedRoots` controls source access, `OutputRoots` controls new saved exports, and the default output directory is `artifacts/exports`. Restart the service after configuration changes.

## Usage

### CLI

After the initial build/configuration, start the shared service and GUI together:

```powershell
.\scripts\Start.ps1
```

Use `Start-Service.ps1 -Background` for the service alone, `Start-Gui.ps1` for another desktop client, and `Stop-Service.ps1` to stop the recorded background service. Finish active jobs and stop watch polling before replacing host binaries.

### Connect an MCP client

Start the service first. The repository supplies portable plugin manifests plus the compatibility `.codex-plugin/plugin.json` and `.mcp.json` layout. Plugin loading includes the bundled FactoryTalk and Logix online-edit workflow skills.

For direct Codex MCP registration, run from this checkout:

```powershell
codex mcp add rockwell-mcp-server -- powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path (Get-Location) 'scripts\Start-Mcp.ps1')
```

The launcher starts the stdio MCP adapter and connects it to the shared service. The REST URL is not an HTTP MCP endpoint. Direct MCP registration does not install the bundled workflow skills; they are available under [skills/](skills/).

Example chat request using the generated L5X fixture:

```text
Register examples/Demo.L5X using its absolute path. Inspect the ladder routines,
preview a rung-comment change, apply it and save a new L5X under the configured
output directory.
```

Inspect/read before editing. Preview binds the requested changes to a source hash; apply requires that preview and its current base hash. Refresh after a stale-revision error. Tool schemas and the live extension catalog describe available operations.

### Desktop workspace

Open a Logix or FactoryTalk export, inspect a component, then **Preview edit**, **Apply preview** and export a new file. **View Designer…** opens the exported-project workspace.

For FactoryTalk displays, **Tag references** shows the current snapshot's extracted references and source expressions. The read-only `ftview_tag_references` extension operation adds search and pagination through the live catalog. Shortcut and parameter resolution remain explicit pending context; extraction does not connect to a PLC or declare tags missing.

Use **Tag / UDT browser…** to search tags, expand structures/arrays and create a watch list from selected values. **Watch lists…** provides polling controls and extracted values; **Trends & recording…** provides graphs, retention and CSV. Saving a watch list does not connect. Lists restart stopped; closing a viewer does not stop already active shared polling.

**Extensions…** exposes the live catalog, operation forms, reload and jobs. Compatible extension updates can be reloaded without restarting Codex. Shared host, SDK binding or contract changes can require a service restart; see [extension updating and rollback](docs/EXTENSIONS.md).

To generate a Studio 5000 Custom Tools entry, run `.\scripts\New-CustomToolsMenu.ps1`. It writes `artifacts/CustomToolsMenu.xml` for manual merging into Studio's installed menu and passes the saved project path to the GUI. The generator does not change the Studio installation.

### Native workflow boundaries

FactoryTalk and View Designer editing operates on exports. Import the prepared content into the intended native project, inspect/build/save as applicable, then re-export for comparison. Direct FactoryTalk binary editing, VPD/VPDR editing and HMI runtime deployment are outside these workflows.

Logix online plans require an explicitly configured and authorized target/action. SDK import-time finalization is available, but the bound SDK has no standalone Test/Untest/Assemble Edits calls. Controller Test mode is different from Test Edits. See [online edits](docs/ONLINE_EDITS.md).

XML checks, file comparisons and successful SDK calls do not establish native HMI acceptance, functional controller behavior or commissioning approval.

## Documentation

| Guide | Contents |
|---|---|
| [Architecture](docs/ARCHITECTURE.md) | Shared service, storage, authentication and process boundaries |
| [Logix SDK](docs/SDK.md) | Bound catalog, native jobs and controller target policies |
| [Online edits](docs/ONLINE_EDITS.md) | Planning, import dispositions and native lifecycle |
| [FactoryTalk View](docs/FACTORYTALK.md) | ME/SE graphics editing and native import handoff |
| [View Designer](docs/VIEW_DESIGNER.md) | Export syntax, property editing and native handoff |
| [Watches and UDTs](docs/WATCHES.md) | Polling, value extraction and controller structure snapshots |
| [Browser and recording](docs/BROWSER_RECORDING.md) | Search, watch creation, trends, retention and CSV |
| [Extensions](docs/EXTENSIONS.md) | Contracts, packaging, live reload and rollback |
| [Exploded projects](docs/EXPLODED_PROJECTS.md) | Format compatibility and loss-prevention checks |
| [Community integrations](docs/COMMUNITY.md) | Candidate libraries, licenses and adoption priorities |
| [Examples](examples/README.md) | Generated demonstration inputs and their limits |

## Validation

Run isolated development verification without a configured native bridge or controller:

```powershell
.\scripts\Verify-Isolated.ps1
```

This checks core operations, REST/MCP transport, extension reload and off-screen desktop rendering in separate state. FactoryTalk schema checks depend on installed schemas. Results stay under ignored `artifacts/isolated-validation/`. See [validation procedures](docs/VALIDATION.md) for outputs, optional offline SDK checks and separate native/live validation requirements.

## API

See [the REST and MCP reference](docs/API.md). The service uses authenticated loopback HTTP (default `http://127.0.0.1:5487`); `.local/connection.json` identifies the running URL and credential file. Keep credentials local.

## Maintainers

[@votecoffee](https://github.com/votecoffee)

## Contributing

Questions and bug reports belong in [GitHub issues](https://github.com/votecoffee/RockwellMCPServer/issues). Focused pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md) for layout, development and validation requirements.

Keep personal/customer notes and deployment records outside the checkout or under ignored `.local/private-docs/`. Only reusable public documentation and generated examples belong in the tracked tree.

## License

[MIT](LICENSE) © 2026 votecoffee. Vendored L5xploder source is MIT © 2025 Rockwell Automation; see [third-party notices](THIRD_PARTY_NOTICES.md).
