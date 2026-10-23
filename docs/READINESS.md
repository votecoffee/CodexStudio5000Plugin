# Connection and engineering readiness

An installed skill does not establish a working MCP connection. Check each layer separately:

| Layer | Evidence | What it cannot establish |
| --- | --- | --- |
| Installed package | Exact plugin identity, release metadata and eleven matching immutable/cache payload hashes | A running adapter or service |
| Configured loader | Plugin flag, bundled-server flag, resolved cached launcher and exactly one enabled route | Tools loaded in an existing chat |
| Serving application | Authenticated loopback health/settings, serving version and effective policy | Native SDK/controller acceptance |
| Resource admission | Retained nonterminal claim count against the configured limit | How many processes are alive or permission to erase records |
| Chat visibility | That chat's actual tool inventory, followed by a successful discovery call | Controller authorization or native validation |
| Native acceptance | Preserved operation, version and exact target-specific results | Acceptance of other versions, targets or operations |

## Read-only diagnosis

The optional diagnostic requires Python 3.11 or newer and the Codex CLI. It uses the selected `CODEX_HOME`, reads installed/cache metadata and performs authenticated GETs to the existing local service. It never starts the service, MCP adapter, Windows SDK service or a controller connection.

```powershell
python .\scripts\codex_connection.py diagnose
```

The report distinguishes the checkout version from the installed cache and serving version. It reports saved engineering settings separately from effective settings; a staged setting does not take effect until the service restarts. Service-query failure remains `unavailable`, rather than an inferred stopped or missing installation. A running Windows SDK service alone does not establish native project readiness.

To include a requesting chat's tool inventory, save this shape in an ignored local file and pass `--visible-tools-json .local/chat-tools.json`:

```json
{"chatId":"requesting-chat","recordedAt":"2026-10-23T12:00:00Z","toolNames":["mcp__rockwell__rockwell_broker_capabilities"]}
```

Supply names from that chat's actual available-tool inventory. The report labels this evidence caller-supplied and does not turn it into a successful chat invocation. Without it, chat visibility is `unknown`. CLI plugin/MCP lists and a direct protocol smoke are separate evidence.

If resource admission is full, retain the journals. Exact private orphan generations can be reviewed through the existing resource ledger recovery workflow. Unknown launches, incomplete identities, native effects, shared/attached claims and dirty documents require their own reconciliation. The diagnostic performs no cleanup and never raises limits to bypass unresolved records.

## Cached-launcher compatibility fallback

Some clients leave `${PLUGIN_ROOT}` literal in a bundled stdio command or `cwd`. Use an explicit host-local binding to the **same immutable installed cached launcher** in that case. Keep the Rockwell plugin and skills enabled; disable its unresolved bundled server while enabling one direct Rockwell route. This remains the app-owned standalone service and has no broker route.

First inspect the plan:

```powershell
python .\scripts\codex_connection.py repair
```

Then apply its exact `configurationSha256`, using a new private backup directory:

```powershell
python .\scripts\codex_connection.py repair --apply `
  --expected-config-sha256 <reviewed-sha256> `
  --backup-directory .local\private-docs\connection-repair
```

The repair verifies package/cache bytes and the runtime pointer before editing. It preserves the exact prior configuration, BOM, line endings, comments and unrelated TOML semantics. Conflicting direct routes, disabled plugins, maintenance holds and bundled tool policies requiring explicit migration cause refusal. Configuration changes after review are refused. Preserve the backup/receipt and inspect configuration readback after a lost result; do not blindly replace it again.

This command changes only the selected profile's connection binding. It does not install or refresh packages, edit immutable cache files, rebuild binaries, restart applications, change engineering settings or reconnect existing watches. The cached bootstrap still delegates to the installed checkout's launcher and binary publication. Keep those installed bytes stable while preparing candidates; a content-addressed bootstrap is not a self-contained binary deployment.

TOML publication preserves UTF-8/BOM and existing LF or CRLF through a binary temporary file. Lone CR/CRCRLF is refused before publication; malformed user text is never silently normalized. Use disposable [configuration byte fixtures](CONFIG_PUBLICATION.md) for investigation. A repaired live profile must remain untouched unless a new binding change is explicitly authorized.

## Reconnect and verify

1. Run the read-only diagnosis and resolve package/configuration or admission findings.
2. If the accepted local service is stopped, start it with `scripts/Start-Service.ps1 -Background` within its authorized profile. Do not use service startup to widen native/controller policy.
3. Refresh the client connection if supported. For changed local plugin files or a chat retaining an old tool inventory, restart the desktop app and open a new chat. Preserve active work first. OpenAI documents separate [plugin and bundled-server enablement](https://developers.openai.com/plugins/build/plugins) and local-plugin reload requirements on that page.
4. In the requesting chat, verify a callable `rockwell_broker_capabilities`, then read `workflow.rockwell-engineering` using its exact discovery arguments. Discover extension operations through `studio_extensions_catalog`. Retain the actual call results, serving version and chat identity.

Do not label an existing chat fixed until its actual tool call succeeds. Compatible extension operations and current workflow content can update behind the stable discovery tools; changing the host's loaded tool inventory is a separate client lifecycle step.

## Enable only the intended engineering capability

Restore the connection independently of engineering permissions. The GUI Settings editor stages explicit settings and reports saved versus running configuration. Starting the fixed stopped SDK Windows service requires `EnableSdkServiceRecovery`; automatic SDK preflight additionally requires `AutoStartSdkService`. Controller operations still require their own configured target, policy and user authorization. Use the [SDK recovery workflow](SDK_SERVICE_RECOVERY.md) and retained request ID; never replay an interrupted SDK job.

Native project operations require the installed bridge and operation/version-specific validation. Native HMI launches use the existing user-owned shell handoff; they grant no ownership of a user's application or documents. Keep native/controller/HMI pilot acceptance distinct from connection repair, offline tests and source/package preparation.
