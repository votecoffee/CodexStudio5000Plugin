---
name: rockwell-engineering
description: Browse and edit Rockwell PLC snapshots, FactoryTalk ME/SE and View Designer exports, multilingual comments and HMI strings, watches, trends, diagnostics and offline transactions through the shared RockwellMCPServer service.
---

Use `studio_extensions_catalog`, then `studio_extensions_describe` for the selected operation's current generation, exact arguments and effect. Invoke it through the matching `studio_extensions_read`, `studio_extensions_preview`, `studio_extensions_apply` or `studio_extensions_control` tool. Catalog entries are discovery metadata, not permission. Refresh after an extension reload or stale generation and review again before another mutation. Never automatically replay an interrupted or indeterminate operation.

The GUI, direct MCP adapter and broker route use the same service. Choose one route for a workflow and preserve its project IDs, source hashes, review tokens and job receipts. Do not switch routes to retry an uncertain write. App locks and revisions apply across clients; broker ownership alone does not fence the GUI or direct clients.

Preserve source files and work on managed copies. Register explicit source paths within configured roots, inspect the target and current revision, preview the requested change, then apply with the returned review/hash guards. Save exports to new output paths. An offline preview, XML/schema check or successful SDK job does not establish native import, runtime behavior or commissioning acceptance.

For PLC comments and descriptions, discover `studio_documentation_*`; for multilingual HMI exports, discover `ftview_languages_*` or `viewdesigner_languages_*`. Preserve keys, existing translations, native metadata and unrelated cells/properties. View Designer language imports can remove omitted languages: retain the full intended language set. FactoryTalk graphics import/export uses the current native application language. Binary ACD editing uses the installed SDK; direct FactoryTalk binaries and View Designer VPD/VPDR editing are unsupported.

Use tag/UDT browsing and watch preflight before creating a watch list. An offline read plan does not connect. Starting live polling requires the user's identified and authorized target; development or metadata discovery does not authorize it. Inspect limits, source revision and backend support. Watch histories/trends/diagnostic results are captured observations with timestamps and gaps, not proof that a machine is safe. Preserve project revisions with diagnostic recordings.

For grouped offline edits, discover `studio_transaction_*`: begin a pinned workspace, stage typed edits, inspect the complete preview and commit its exact review hash once. Abort discards staged work. These transactions cover one offline workspace; native SDK jobs and controller operations are outside the transaction. Inspect retained receipts before retrying a commit.

For online Logix import/test/finalize work, load the `logix-online-edits` guide before proceeding. For native FactoryTalk graphics import/re-export work, load `factorytalk-studio`. The broker publishes these as workflow capabilities; standalone installations package them as skills. Honor hands-off UI instructions and record native observations separately from API results.
