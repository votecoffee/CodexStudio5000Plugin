# Timestamp release identities

One source checkpoint uses one fixed `YY.MMDD.HHmmss` display version in **America/New_York**. `release-version.json` is the release identity; `Stamp-Release.ps1` synchronizes its generated MSBuild properties, package manifests and embedded Python metadata. Stamping is explicit. Build, verification, installation, CLI reporting and MCP startup never create a version from the current clock.

| Identity | Example | Use |
| --- | --- | --- |
| Padded display | `26.0101.000001` | Health/MCP server info, product/informational version, CLI and release metadata |
| Canonical package | `26.101.1` | Strict SemVer plugin/extension packages, Python PEP 440, NuGet and cache identity |
| Bounded assembly/file | `26.101.0.1` | Four fields: `YY.MMDD.HHmm.ss`, each numeric and within .NET limits |

For `26.1011.172201`, the canonical package is `26.1011.172201` and assembly/file version is `26.1011.1722.1`. `HHmmss` is valid as a package patch component but can exceed the 65535 assembly/file field limit. The human timestamp remains fully padded; package and assembly fields are decimal integers without padding. Numeric three-part comparisons identify padded and canonical releases semantically, so `26.0101.000001` and `26.101.1` do not create a false update notification. Protocol and third-party SDK/framework versions retain their own schemes and pins.

## Source and build flow

```powershell
.\scripts\Stamp-Release.ps1
.\scripts\Stamp-Release.ps1 -Check
.\scripts\Verify-ReleaseVersion.ps1
dotnet test tests/Studio5000.Tests/Studio5000.Tests.csproj --filter FullyQualifiedName~ReleaseVersionTests
```

Stamp once, update the current README display, review and commit the checkpoint. The committed `ReleaseVersion.props` supplies app assembly attributes; Core reads them for app/MCP reporting. The three .NET service/MCP/stdio executables accept `--version` without connecting or starting a host. The optional Python CLI also accepts `--version`; its `__version__` is canonical and `__display_version__` padded. Python wheels and plugin packages carry the same `release-version.json`. Build manifests and manager installation metadata keep both forms. Native binaries outside an authorized build keep their historical identities; versioning never establishes native validation.

`Stamp-Release.ps1 -DisplayVersion ...` is an explicit checkpoint override for reproducible work; it validates the actual calendar/time and retains an existing identical checkpoint's instant. Use generated disposable roots/profiles for packaging boundary tests. Real plugin-loader checks exercise early-month/midnight canonical versions in an isolated Codex home. They do not enable the user's plugin.

The two-digit year assumes 2000–2099. New York DST fallback, clock corrections or two checkpoints in the same second can repeat a display value. Do not republish changed bytes under that identity; select a distinct checkpoint. Immutable package hashes and retained release receipts remain necessary.

## Deployment boundaries

Use normal guarded installation only when authorized and idle. Preserve user configuration, saved state, current user-owned applications, original packages and uncertain journals. Retain the old/new mapping under ignored `.local/private-docs/`. A source timestamp change grants no new native/controller/CIP/SCM/shared-host access. Released offline profiles keep their effective refusals during an update. Verify source, product/informational/file attributes, MCP and CLI reports, installed/cache package identities and live app reporting independently. Existing chats may retain the prior connected frontend until refreshed; never infer current desktop-chat tool activation from loader metadata alone.
