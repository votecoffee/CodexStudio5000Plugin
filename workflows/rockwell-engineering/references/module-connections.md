# Module connection timing review

Use the shared service's `studio_module_connections`, `studio_module_connections_preview` and `studio_module_connections_apply` operations through the matching read/read/apply extension channels. Describe the current generation/schema first. General `studio_preview_changes` attribute edits remain restricted to Tag/Routine scope; they are not the module-configuration route.

In the GUI, choose **Diagnostics & workflows → Module connection timing**, browse the source project, and copy exact identities and before-values into the prepared preview editor. Apply publishes a **new REVIEW ONLY project** and immutable JSON receipt. The registered file and original managed project remain unchanged. No SDK/native job or controller connection is started.

## Browse

Supply `request:{projectId,expectedSha256,query?,offset:0,limit:100}`. The hash-bound inventory returns exact module and connection names, positional XPath, catalog number, existing Type/RPI/TimeoutMultiplier/NetworkDelayMultiplier/ReactionTimeLimit fields and explicit safety evidence. It reports unsupported connection elements separately; RackConnection and other shapes are not edited. Pages contain up to 256 rows and 4 MiB. Names identify exactly one existing `Controller/Modules/Module/Communications/Connections/Connection` pair; no caller XPath is executed by these tools.

## Preview and apply

Preview uses:

```json
{
  "request": {
    "projectId": "<managed-project-id>",
    "expectedSha256": "<current-project-sha256>",
    "safetyHandling": "block",
    "edits": [
      {"moduleName":"DemoModule","connectionName":"Input","attribute":"RPI","expectedValue":"10000","value":"20000"}
    ]
  }
}
```

Only existing `RPI`, `NetworkDelayMultiplier` and `ReactionTimeLimit` attributes are editable, at most 200 exact unique fields per review. Source SHA and parsed before-values must match exactly. Names, connection type/path, timeout multiplier, data/forces, signatures, unknown attributes and unselected bytes remain unchanged. UTF encoding/BOM, quote style and line endings are preserved as `scalar-byte-spans-v1`.

Requests use invariant decimal **strings**, preserving the chosen spelling. RPI is a positive UInt32 in exported microseconds; NetworkDelayMultiplier is integer percent 10..600; ReactionTimeLimit accepts unsigned decimal 0..5500032 in its native exported representation, including fractional values. These structural envelopes do not establish module-specific supported values. No unit conversion or timing calculation is performed. The export manual documents RPI and safety connection fields: [1756-RM014D, module connections](https://literature.rockwellautomation.com/idc/groups/literature/documents/rm/1756-rm014_-en-p.pdf).

Inspect every change's complete original `before` and combined final `after` timing context, source hash, safety findings and `reviewSha256`. Related timing attributes stay unchanged unless independently listed in the request. Explicit safety evidence blocks changes by default; for an authorized review copy choose `safetyHandling:"review-only"` and preview again. Evidence includes safety connection type/fields, module safety flags/network/configuration and safety class. Missing markers do not establish that a module is nonsafety.

Apply with `request:{preview:<the exact reviewed preview request>,expectedReviewSha256:<reviewSha256>}`. The service recomputes the source and complete review before publishing. Stale hashes/before-values, duplicate/unsupported fields and unresolved safety handling reject apply. Retain the returned project and receipt IDs/hashes. Use normal artifact/export tools to save them to new paths. Do not automatically repeat an uncertain apply; inspect retained state first.

## Acceptance boundary

All outputs remain review copies with `nativeAcceptance:"not-performed"`. Module-profile range/coupling, output safety task timing, reaction-time calculations and retained signature applicability require independent review. Native software can recalculate timing fields when related values change; the service does not predict or silently reproduce that behavior. When separately authorized, use existing offline SDK import/export/readback and the `native-readback` profile to compare actual retained values. A file comparison does not establish native provenance, safety validation, controller behavior or deployment acceptance.
