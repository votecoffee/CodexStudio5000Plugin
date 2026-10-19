# Contributing

Questions, bug reports and focused pull requests are welcome through [GitHub issues](https://github.com/votecoffee/RockwellMCPServer/issues) and pull requests. For larger changes, describe the proposed behavior in an issue first.

## Repository layout

| Directory | Contents |
|---|---|
| `src/` | Shared operations, service, clients, native bridge and extension hosts |
| `assets/` | Application artwork and reproducible icon packaging instructions |
| `tests/` | Unit/integration checks and generated extension fixtures |
| `scripts/` | Build, launch, extension packaging and verification commands |
| `docs/` | Reusable architecture, API, feature and verification guides |
| `examples/` | Small generated demonstration inputs with no customer data |
| `skills/` | Reusable agent workflows and their references |
| `third_party/` | Vendored source with its license and provenance |
| `extensions/` | Public extension instructions; installed package directories are ignored |

Keep the root [README](README.md) focused on current capabilities, installation and usage, following [Standard Readme](https://github.com/RichardLitt/standard-readme). Update the relevant feature/API guide when behavior changes.

## Private and generated files

This is a public repository. Keep personal/customer material outside the checkout or in the ignored `.local/private-docs/` directory. Use `artifacts/` for generated build, export and verification results. Local configuration belongs in ignored `service.local.json`; `service.example.json` contains portable defaults only.

Do not commit controller addresses, plant/application inventories, personal filesystem paths, chat handoffs, deployment receipts, credentials, customer PLC/HMI projects or native screenshots/exports. Preserve existing private notes locally before replacing them with reusable public instructions. Gitignore rules do not protect files that are already tracked; review the staged diff and never force-add private directories.

Examples and regression fixtures must be generated or independently approved for public distribution. Use placeholders for communications paths and documentation-reserved addresses for inert tests. Proprietary Rockwell SDK binaries, installed schemas and activation files must stay local. Preserve third-party copyright/license notices and update the provenance manifest for vendored source changes.

Before committing, inspect:

```powershell
git status --short
git diff --cached --check
git diff --cached
git check-ignore .local/private-docs/example.md service.local.json artifacts/example.json
```

Do not paste credentials, raw local configuration or private validation output into public issues. A report should include relevant software versions, reproduction steps, expected/actual behavior and a minimal redacted or generated fixture.

## Development and validation

Use Windows and the .NET 8 SDK. Keep domain operations in the shared service/Core so MCP, REST and the desktop retain the same behavior. Compatible feature additions can use the [extension contract](docs/EXTENSIONS.md); host/contract changes may require a restart. Follow the existing C# and PowerShell style.

```powershell
dotnet build RockwellMCPServer.sln -c Release
.\scripts\Verify-Isolated.ps1
.\scripts\Verify-CodexIntegration.ps1
```

The isolated verifier has its own service/state, no native bridge and disabled controller execution. See [validation instructions](docs/VALIDATION.md) for expected outputs, conditional schema checks and optional native procedures. Report what was actually tested, including skipped checks and native boundaries. Ordinary development does not authorize controller connections or native project changes.

Keep commits focused and include the relevant validation in the pull request. Changes should preserve original inputs, reject stale mutations and avoid automatic retries of indeterminate native work.
