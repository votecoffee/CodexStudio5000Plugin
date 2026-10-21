# Standalone Codex integration

RockwellMCPServer ships its own Codex plugin. It connects directly to the app-owned stdio MCP adapter, which calls the same authenticated service as the GUI and REST clients. Installation never selects or registers an external broker.

The optional [app-owned framework host](FRAMEWORK.md) remains a separate package for clients that need its fixed discovery interface. It does not change the standalone plugin or require a universal daemon/plugin.

## Install and configure

`Install.cmd` defaults to `-CodexIntegration Standalone`. Inspect or configure the plugin separately:

```powershell
.\scripts\Configure-Codex.ps1 -PlanOnly
.\scripts\Configure-Codex.ps1
.\Install.cmd -CodexIntegration None
```

| Choice | Behavior |
|---|---|
| Standalone (default) | Install the app plugin when absent. Refresh enabled app-owned packages; stage disabled updates while preserving their disabled setting. |
| None | Skip Codex integration without changing any installed plugin. Every invocation must explicitly select None to skip installation. |

`Auto`, `Broker`, `BrokerRoot` and `AcceptBrokerRoute` are no longer supported. Removed options fail parameter validation before installation work starts. Other installed plugins do not affect the choice. Existing plugins belonging to another installation are retained.

Receipts are per `CODEX_HOME` profile under ignored `.local/codex-integration/`. A legacy broker receipt is never invoked or reused as a routing policy. Migration to standalone requires an idle app, archives the exact receipt bytes under `legacy/`, then records standalone. External registrations, policy and recovery state remain untouched; retire any independently configured external registration in its owning application after reconciling its work. Plan-only mode writes no receipt or package.

## Package and update lifecycle

The installer publishes an immutable, content-addressed package under `.local/codex-marketplace/packages/`. Its public-content allowlist includes portable/compatibility plugin manifests, MCP configuration, the MIT license, three workflow skills, seven supporting references and a launcher/runtime pointer. Projects, service credentials, state and build outputs are excluded. Identical content reuses the package; previous packages remain intact.

Only an app-owned package with the exact plugin identity and matching marketplace source is refreshed. A disabled plugin stays disabled: its update is staged without invoking the enabling `plugin add` command. Existing native per-server settings are retained. An enabled app-owned refresh requires an idle app; first-time installation only publishes a new package and can run with `Install.cmd -SkipBuild` while the independent service is working. Binary rebuilds still require stopped applications.

Start the service with `Run.cmd` or `Start-Service.ps1`. A new Codex chat may be needed to load updated plugin tools/skills. Compatible extension operations continue to reload through the live app catalog without reinstalling the plugin. The REST service URL is not an HTTP MCP endpoint.

## Skills appear but tools are missing

Plugin installation, skill discovery and MCP tool exposure are separate checks. Portable packages use root `mcp.json`, which must declare `$schema: "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json"`. Keep `.mcp.json` for compatibility; it does not establish that Codex accepted the portable file. A missing schema can leave the skills installed while the MCP server is absent.

Run `codex mcp list --json` to check Codex's actual configuration loader. The output should contain `rockwell-mcp-server`; an intentionally disabled per-server setting stays disabled. `codex plugin list --json` alone cannot establish MCP discovery. The generated-profile CLI check below verifies both states without starting the engineering app or reading projects.

After reinstalling a corrected package, open **Settings → MCP servers** in the desktop app and select **Restart**. Use `/mcp` in the composer to check the connected servers, then discover `studio_extensions_catalog` in the engineering chat. If the local package is still cached or missing, save current work, restart the desktop app and begin a conversation with the plugin enabled. These are the supported host refresh steps in [MCP configuration](https://learn.chatgpt.com/docs/extend/mcp) and [local plugin packaging](https://developers.openai.com/plugins/build/plugins). Reinstalling or restarting Rockwell's background service cannot by itself update another chat's tool inventory.

The local marketplace is `rockwell-mcp-server-local`, and the installed identity is `rockwell-mcp-server@rockwell-mcp-server-local`. A public-directory search is not proof that this local installation is absent. Do not add another broker, duplicate server or private transport workaround to compensate for a missing tool. Host tool exposure must be verified in the chat that will perform engineering work. If the user cannot refresh the host and asks to continue through the installed service, use the [documented authenticated REST fallback](../skills/rockwell-engineering/references/structural-edits.md#authorized-rest-fallback) within the same authorized engineering scope. That fallback preserves the ordinary review and receipt guards; it does not repair or establish MCP exposure or override an explicit access restriction.

## App discovery and versioned guides

These read-only tools expose the same workflow content used by the standalone plugin:

| Tool | Purpose |
|---|---|
| `rockwell_broker_capabilities` | Normalized installation capability catalog v1 with active extension operations and three workflow entries. Its historical name is retained for API compatibility; it performs no broker registration or routing. |
| `rockwell_read_workflow` | Read a named workflow at its exact catalog revision, including related tool names and hash-bound reference readers. |
| `rockwell_read_workflow_reference` | Read a named packaged reference at the exact workflow revision and SHA-256. |

Operation IDs `<extensionId>.<operation>` point to `studio_extensions_describe` with the pinned generation. Workflow IDs are `workflow.rockwell-engineering`, `workflow.logix-online-edits` and `workflow.factorytalk-studio`. Shared Core embeds the same Markdown sources packaged by the standalone plugin and adds app invocation guidance.

Catalogs contain installation metadata, never selected projects, targets, credentials or private workspaces. Reading a catalog can initialize installed extension metadata workers; it never reloads a module or executes a domain operation. Incomplete/oversized catalogs fail without partial hints. Entries are discovery-only. Preserve app generations, workflow revisions and target/review hashes independently. Named reference readers reject caller paths, stale hashes/revisions and external fetching. Publication grants no authorization and cannot install code, change policy or retry uncertain work.

For API compatibility the authenticated REST paths remain `GET /api/broker/capabilities`, `/api/broker/workflow?workflowId=...&expectedRevision=...` and `/api/broker/reference?workflowId=...&expectedRevision=...&referenceId=...&expectedSha256=...`. These metadata endpoints use shared Core and do not route operations through a broker.

## Validation

```powershell
.\scripts\Verify-CodexIntegration.ps1
.\scripts\Verify-CodexPluginCli.ps1
.\scripts\Verify-Isolated.ps1
```

Installer fixtures mock Codex commands and cover standalone installation, immutable packages, ownership, enabled/disabled upgrades, removed-option rejection, legacy receipt archival, app-only installation and missing CLI behavior under PowerShell 7 and Windows PowerShell. The optional real CLI script checks install, updated cache content, retained per-server policy, staged disabled updates and preserved old packages using generated content and a disposable profile. It starts no app or native worker. Normal Codex settings remain untouched. Isolated REST/MCP checks verify the metadata contract and exact workflow/reference hashes. Native Studio/controller/HMI acceptance remains separate.
