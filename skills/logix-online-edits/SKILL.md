---
name: logix-online-edits
description: Prepare Logix online rung or routine imports through the shared service and carry accepted edits through Studio 5000 Test, Untest and Assemble using observed native UI. Use for PLC online-edit workflows, including existing accepted edits; FactoryTalk HMI work uses its own skill.
---

Use the selected Rockwell app connection. Call `rockwell_broker_capabilities`, select `workflow.logix-online-edits`, and call `rockwell_read_workflow` with the returned exact revision. Fetch the required native workflow reference using its returned `rockwell_read_workflow_reference` arguments and SHA-256. Discover/describe the exact live SDK and import tools before using them; keep this app route throughout the operation.

Development/planning does not authorize a controller connection. Honor the user's exact controller, saved project, program/routine, tag scope and hands-off UI instructions. Preserve source/export hashes, review plans, jobs and native observations. SDK controller Test mode is not Test Edits; do not invent standalone Test/Untest/Assemble APIs or treat SDK completion as native/runtime acceptance. Never reimport or retry an uncertain write to discover its outcome. A guide is not approval. If the connection or native evidence is unavailable, preserve the prepared work and unresolved outcome for reconciliation.
