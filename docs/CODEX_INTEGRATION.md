# Codex plugins and Universal Tools

RockwellMCPServer remains an independent desktop/REST/MCP application and continues shipping its own Codex plugin. Universal Tools from [CodexMCPBroker](https://github.com/votecoffee/CodexMCPBroker) provides another route to the same app-owned service. Broker users can use one Universal Tools plugin; standalone users need no broker.

## Installation choices

`Install.cmd` defaults to `-CodexIntegration Auto`. Inspect or select integration separately:

```powershell
.\scripts\Configure-Codex.ps1 -Route Auto -PlanOnly
.\scripts\Configure-Codex.ps1 -Route Standalone
.\Install.cmd -CodexIntegration None
```

| Route | Behavior |
| --- | --- |
| Auto, existing receipt | Retain that route. A failed broker prerequisite never installs a fallback or replays work through another route. |
| Auto, existing app plugin | Preserve standalone, including a disabled choice. Never remove/disable it because a broker appears. |
| Auto, fresh profile | Prefer enabled compatible Universal Tools with explicit acceptance for this app version; otherwise install standalone. A conflicting registration blocks selection. |
| Standalone | Install the app plugin when absent; preserve existing plugin settings. No broker required. |
| Broker | Require enabled Universal Tools, matching initialized local runtime, compatible versions and accepted workflow parity. Register the app when absent; skip the app plugin. |
| None | Skip optional integration, retain existing plugins/registrations and preserve the choice on future auto installs. |

Receipts are per `CODEX_HOME` profile under ignored `.local/codex-integration/`. Detection uses `codex plugin list --json`, installed/enabled flags and exact `universal-tools@universal-tools-local` identity. Cache folders alone do not establish availability. This adapter supports the broker's local `plugins/universal-tools/runtime.json` layout, plugin/runtime **0.11.1 or later** and `shared` sessions. Installed plugin version, runtime version and app acceptance each must pass; an updated checkout alone does not update installed packages. A different distribution needs an assessed adapter.

Standalone packages use an explicit public-content allowlist under `.local/codex-marketplace/packages/`. Identical bytes reuse an immutable package. Only manifests, the MIT license, Markdown skills/references, a launcher and its private runtime pointer enter the plugin cache. Never register the whole checkout as a plugin source: it contains local files and build outputs. Existing installations are preserved; refresh/reinstall them through Codex when packaged guidance changes. Keep the app checkout in place or rerun installation after moving it.

Missing Codex tooling or an integration error in Auto mode leaves the application installed and reports the limitation. Explicit route failures are errors. Plugin installation/new skills can require a fresh chat; see [OpenAI's packaging documentation](https://developers.openai.com/plugins/build/plugins).

## Broker acceptance and selection

Complete the broker's [migration acceptance](https://github.com/votecoffee/CodexMCPBroker/blob/main/MIGRATION.md) in a disposable profile first. In a **Universal Tools-only fresh chat**, verify workflow discovery, guide/reference availability, exact tool/target/review bindings, required host features and confirmation behavior. Protocol fixtures do not prove native UI, controller behavior or Codex skill activation. Keep native acceptance separately scoped; development does not authorize a controller connection.

Finish/reconcile active jobs, prepared operations and uncertain outcomes, close clients and stop the app. Explicit selection records the operator's acceptance for this app version:

```powershell
.\scripts\Configure-Codex.ps1 -Route Broker -BrokerRoot 'C:\Tools\CodexMCPBroker' -AcceptBrokerRoute
```

`-AcceptBrokerRoute` acknowledges completed workflow checks and reconciliation; it does not perform or certify them. It is also available with `Install.cmd -CodexIntegration Broker`. Auto can use it for a fresh profile, while preserving any existing standalone choice. Acceptance binds the app/plugin/runtime versions and broker installation/state; changes require reassessment. Switching from a recorded broker route back to Standalone uses the same acknowledgment after reconciliation, and retains broker registration/recovery evidence. These commands never remove an existing app plugin, change native approval settings or fail over a running operation.

The generated manifest is `.local/broker/rockwell-mcp-server.service.json`. It launches absolute PowerShell and app `Start-Mcp.ps1` paths; start the app service with `Run.cmd` or `Start-Service.ps1`. Registration uses the broker's administrative CLI without starting/reloading it or probing native targets. Existing transport, shared session and catalog provider must match; conflicting/disabled registrations fail without replacement. Administrator write policy remains byte-for-byte unchanged on reinstall.

New registrations are **read-only** (`allow_writes: false`, `allowed_write_tools: []`). A broker administrator can review/edit the local manifest and intentionally replace it with the broker's `Register-App.ps1 -Replace`. Enable only required exact outer tool names. For example, staging/applying extension changes requires `studio_extensions_preview` and `studio_extensions_apply`; native control uses `studio_extensions_control`. An outer-tool allowlist cannot restrict individual inner operations. App scope, effects, hashes, previews and controller gates remain authoritative. The installer never broadens policy.

Start the app, call `refresh_services` in Universal Tools, then search/describe the app tools. Shared broker sessions serialize adapter calls; other clients use the same service coordinator. Broker ownership does not fence the GUI/direct clients. Failures, lost sessions and uncertain writes require reconciliation before a deliberate route change.

## Discovery and workflow contract

Three explicitly read-only tools augment the direct MCP surface (59 tools total):

| Tool | Purpose |
| --- | --- |
| `rockwell_broker_capabilities` | Normalized capability catalog v1 with active extension operations and three workflow entries. |
| `rockwell_read_workflow` | Read an exact named guide revision with instructions, tool bindings and reference hashes. |
| `rockwell_read_workflow_reference` | Read a named packaged UTF-8 reference with workflow revision/SHA-256 checks. |

Register `capability_catalog: {"schema_version":1,"tool":"rockwell_broker_capabilities","arguments":{}}`. Operation IDs `<extensionId>.<operation>` point to `studio_extensions_describe` with pinned generation. Workflow IDs are `workflow.rockwell-engineering`, `workflow.logix-online-edits` and `workflow.factorytalk-studio`. Core embeds the same Markdown sources packaged by the standalone plugin and adds explicit broker invocation guidance.

Catalogs contain installation metadata, never selected projects, targets, credentials or private workspaces. Normal catalog access can initialize installed extension metadata workers; it never reloads a module or executes a domain operation. Incomplete/oversized catalogs fail without partial hints. Entries are discovery-only. Preserve broker tool/capability revisions, app generations, workflow revisions and target/review hashes independently. Named reference readers reject caller paths, stale hashes/revisions and external fetching. Publication grants no authorization and cannot install code, change policy or retry uncertain work.

Authenticated REST equivalents: `GET /api/broker/capabilities`, `/api/broker/workflow?workflowId=...&expectedRevision=...` and `/api/broker/reference?workflowId=...&expectedRevision=...&referenceId=...&expectedSha256=...`. MCP and REST use shared Core.

## Validation

```powershell
.\scripts\Verify-CodexIntegration.ps1
.\scripts\Verify-Isolated.ps1
# Broker development Python with its locked dependencies:
C:\Tools\CodexMCPBroker\.local\development\Scripts\python.exe scripts\verify_broker.py --broker-root C:\Tools\CodexMCPBroker --workspace-root <isolated-profile>
```

Installer fixtures mock administrative commands and cover route preservation, fallback, conflicts, acceptance and immutable package content under PowerShell 7 and Windows PowerShell. The optional Python check uses real broker source/protocol with its own state, generated L5X and a profile from `Verify-Isolated.ps1`; all native bridges/targets must be disabled. It checks the fourteen-tool broker frontend, guides/references, inner schemas, read/write policy, review guards and idempotent app receipts. Normal registrations/Codex settings remain untouched. Fresh-chat/native acceptance remains an operator task.
