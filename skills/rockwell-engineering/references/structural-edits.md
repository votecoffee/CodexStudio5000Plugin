# Scoped byte-preserving structural review

Use `studio_structural_edit_context` (read), `studio_structural_edit_preview` (preview), `studio_structural_edit_apply` (apply) and `studio_structural_edit_status` (read) through the matching extension channels. Discover the live generation/schema first. The GUI exposes the same operations under **Diagnostics & workflows → Scoped structural batch**.

General `studio_preview_changes` structural/mixed edits and ordinary XML transactions retain `document-serialization-v1`. This separate workflow uses `structural-byte-spans-v1`: only selected spans change, including on removals. Untouched annotation internal CRLF/CDATA, tag data/definitions, protected libraries, encoding/BOM and other source bytes remain exact. It operates on one pinned managed source and creates a separate **REVIEW ONLY** copy, immutable L5X and receipts. It starts no native job or controller connection.

## Inspect and declare the review

Context requires `request:{projectId,expectedSha256,expectedRevision,xPath?,xPaths?,query?,offset:0,limit:100}`; inspect the live schema for exact casing and installed support. Without paths it pages named tags, routines and UDTs; with `xPath` it returns one exact element or existing container. With `xPaths` it resolves 1..128 distinct, nonempty exact paths against one hash-verified snapshot parsed once. Do not combine `xPaths` with `xPath` or `query`. Every path must select exactly one existing element; an invalid, missing or ambiguous path fails the whole read with its zero-based index. No partial successful context is returned. Results preserve requested path order, with the ordinary `offset`/`limit` pagination and `total`; use `limit:128` to return an entire batch up to the maximum. Each path is at most 8192 characters and the response remains bounded to 4 MiB. This is a read, not a change to edit scopes or approval.

For a scoped review, gather exact targets and protected/container paths first, then request them together instead of making one context call per element. Keep a single initialized MCP client/session open for catalog, describe and successive calls rather than starting the stdio process for each read. The maintained MCP adapter supports that lifecycle. If using the explicitly authorized REST fallback below, reuse the authenticated HTTP client and send the same batch body directly to the existing context route. Do not reuse a hash or generation after the source/catalog changes, and never automatically replay an uncertain write to recover a connection.

Batch context requires shared host 0.35.3 or later. The extension sends batches to `/api/structural-edits/context/batch`; an older host rejects that route instead of silently ignoring the new `xPaths` field. Use that route for direct REST batches too. Update the idle shared service before activating the new extension package; reloading only the extension cannot add the host implementation. Single-path reads retain their original route and contract.

Use returned `elementSha256` for each edit/protection. Existing `studio_read_element` supplies component XML; it serializes its response, so use context fingerprints rather than hashing that returned XML as source bytes. Tag context also reports a raw stored-value-set hash.

Bind a real nonempty JSON/Markdown/text plan path or artifact up to 1 MiB with its exact SHA-256. The service verifies bytes and never executes or interprets the plan as authorization. Declare exact program/routine pairs, controller/program tags, UDT names, optional protected paths/fingerprints, `safetyHandling` and incomplete-binding acknowledgment. Source revision and whole-file SHA must both match.

Supported inline operations are `add_element`, `remove_element`, `replace_element` and `set_text`. Components are scoped program routines, RLL rungs, ST lines, tags and UDTs; text must be within scope. Replacement retains component type, Name and Number. Use separate remove/add to change identities. Adds target an existing container; multiple additions to an empty/self-closing container are supported. XML values contain one component, not an L5X document; outer XML whitespace is trimmed. Limits are 200 edits, 1 MiB per inline/reference component and 4 MiB total text. Overlapping edits are rejected.

Whole Program, existing AOI, module, task and safety-configuration replacement is unsupported. Existing AOIs, encoded/signed objects, force data, modules/tasks/SafetyInfo and declared protected subtrees are protected from overlapping patches. Inline fragments cannot introduce those protected classes. Native library upgrades are not inferred.

## Exact reference additions

Previously absent reviewed AOIs and UDTs can use `add_reference`, without inline `value`. This accepts an original native reference or a separately reviewed application-library L5X; a source hash does not establish authorship or commissioning acceptance. Declare `scope.referenceAdditions` entries with unique `key`, `kind:"aoi"|"udt"`, exact `name`, `source:{path|artifactId,sha256}`, source `xpath` and `expectedElementSha256`. Register a preserved reference L5X and inspect it through context to obtain raw node fingerprints when needed. Reference L5X inputs are at most 64 MiB each / 128 MiB distinct total.

The edit targets the destination controller's existing AddOnInstructionDefinitions or DataTypes container and specifies `referenceKey`. Source file and node hashes are rechecked, and the raw node text is copied in the target encoding. Existing identities cannot be overwritten/upgraded by this path. Required visible UDT/AOI dependencies must exist after the batch; exported UDT member layouts must match the reference, preventing implicit different-sized struct substitution. Opaque dependencies and full compiler compatibility remain unproved. Review safety scope for safety AOIs; copied signatures are not native acceptance evidence.

## Saved-state decisions

Every whole-tag addition/removal/replacement requires a `scope.valueDispositions` entry: `tag:{tagName,programName?}`, `action`, nonempty `reason`, and optional `fromTag`. Actions are:

| Action | Enforced local condition |
|---|---|
| `remove` | Existing tag is absent after the batch. |
| `preserve` | Existing tag remains with the same declared type/dimensions and exact stored representations. |
| `replace-reviewed` | Existing tag remains; its explicit complete replacement is reviewed. |
| `initialize-reviewed` | New tag; initialization and any source-state derivation are explicitly reviewed. |
| `copy-values` | New tag copies exact raw values/type/dimensions from the specified source tag. |

The preview retains before/after value hashes, source tags and reasons. Untouched tags are automatically committed by raw-byte hashes; scalar comment/logic edits cannot silently change retained data representations. UDT layout changes require explicit whole-tag treatment for affected direct/transitive instances. The service does not repack raw values, reset timers, select equipment ownership, clear faults or infer safe rearming. Retain uncertain saved reservations/states in the plan until reviewed. A future export/rebase must reevaluate intersecting state and conflicts rather than reuse old initialization decisions blindly.

## Retain, apply and reconcile

Preview creates an immutable JSON receipt and returns `reviewSha256`, `resultSha256` when applicable, preservation commitments, reference bindings, value dispositions and findings. Known remaining deleted-tag/alias/UDT references, new missing main/literal JSR targets, protected-layout dependencies and missing state/safety acknowledgments block apply. Inherited routine-reference defects remain labeled separately. Lexical checks are bounded and do not establish complete compiler, AOI, FBD/SFC, dynamic, external or HMI binding.

Apply requires the exact preview request, review hash, preview receipt ID and receipt SHA. It rechecks source/plan/reference bytes and all guards before publishing. Retain the separate project, immutable candidate L5X and apply receipt. A repeat of a completed exact apply returns that retained result. An incomplete durable intent is `applying-indeterminate` and is never replayed automatically. Read status using receipt ID/SHA after any lost response. Receipts remain historical evidence even if another client later advances a managed project.

Source files and source revisions remain unchanged. Separate projects are not one atomic multi-project transaction. Native import/build/export/readback use existing offline SDK operations only when separately authorized, followed by named validation/retained readback receipts. `native-readback` compares supplied actual output with the intended candidate; it does not establish provenance or native safety/deployment acceptance. Changed tag layouts/owned values require their own explicit plan/validation policy; routine-only preserve-fresh merging does not cover arbitrary structural migrations.

## Authorized REST fallback

If the chat's MCP tools are unavailable and the user asks to continue through the installed shared service, its documented authenticated REST API is a supported front end. Keep application design and execution in the engineering chat; server-development chats improve reusable tools and workflows. A connection failure alone grants no additional authority. Honor any explicit tool/access restriction; do not use another transport to evade one. Existing authorization for the same offline source, edit, review and output remains applicable without inventing another approval step.

Identify the intended installation and running connection using the application's `.local/connection.json`; a packaged installation's trusted `runtime.json` identifies its app root. Use the local credential file named by `tokenFile` as `Authorization: Bearer <token>`, without displaying or recording the credential. Keep loopback binding, authentication, path policy, scope, locks, revisions and audit enabled. Neither reading a connection file nor selecting REST grants controller, native SDK, settings or filesystem access. Do not add a private endpoint, duplicate broker or alternate policy.

The same typed service requests apply; send the request object directly, without the MCP `{call:{...}}` wrapper:

| Step | Documented REST route | Guard/result |
|---|---|---|
| Register/freeze | `POST /api/projects` with `{path}` | Retain project ID, current SHA and revision; original remains unchanged. |
| Exact context | `POST /api/structural-edits/context` | Same `projectId`, `expectedSha256`, `expectedRevision` and optional single `xPath`. |
| Batch context | `POST /api/structural-edits/context/batch` | Same pinned source and `xPaths:[...]`, `offset:0`, `limit:128`. Requires host 0.35.3+. |
| Retained preview | `POST /api/structural-edits/preview` | Exact `StructuralEditRequest`, actual plan hash, explicit scopes/reference additions/value dispositions. Retain preview receipt ID/SHA and review hash. |
| Reviewed apply | `POST /api/structural-edits/apply` | `{preview,expectedReviewSha256,previewReceiptId,previewReceiptSha256}` with the identical reviewed request. Returns a separate project, immutable candidate L5X and receipt. |
| Reconcile | `GET /api/structural-edits/previews/{previewReceiptId}?expectedSha256={previewReceiptSha256}` | Read retained `previewed`, `applying-indeterminate` or `applied` state before another action. |
| Export when needed | `POST /api/projects/{id}/exports` with `{expectedSha256,format:"l5x"}` | For a workflow returning only a managed project. Structural apply already returns its candidate L5X artifact. |
| Save requested copy | `POST /api/artifacts/{artifactId}/save` with `{path:"<absolute-new-name.L5X>"}` | Saves a new file under effective output policy; no overwrite option. Retain path, SHA and byte count. |
| Artifact readback | `GET /api/artifacts/{artifactId}` | Hash-verified immutable artifact bytes; compare SHA with the saved file. |

REST calls use the same Core operations as the GUI/MCP adapter. Switching front ends does not reset review state or authorize replay. After a lost apply response, read retained status: use an already recorded result for `applied`; stop at `applying-indeterminate`. Never restart a mutation with a new request to escape that state. If a file-save reply is lost or `output_exists` is returned, inspect the exact destination and expected artifact hash before choosing a next action; preserve the existing file.

## Deliver the named L5X, then honor native ownership

Once the authorized edits and required scoped checks finish, save the requested named L5X immediately to the user's requested source/output folder, when allowed by the effective output policy. Preserve the original and use a distinct filename. Read back the destination bytes and compare their SHA with the candidate/export artifact; retain the exact path/hash and source/review receipts. Report that the copy is available for the user's import, together with any additional checks still running or unsupported. Do not hold it for optional SDK work or silently redirect it to an internal artifact directory. If a required check failed, report that limitation and do not label the copy accepted.

When the user owns native Studio import, leave that step with the user. Native SDK open/save/export/build is separate work requiring the appropriate authorization and version support; it is not a default gate for a requested edited L5X. Reuse valid retained checks when the source, rules, tool version and coverage still match. Bind later native observations or supplied readback to the exact delivered file; do not equate successful import/save, offline validation or an SDK binding with Verify Controller, runtime or safety acceptance.

For a separately requested native job, retain its ID, exact input hash, installed SDK/Logix versions, method/arguments, per-call diagnostics and output identities. An unsupported `BuildAsync` attempt is method/version/target-specific evidence, not a failed XML edit or proof that native import is impossible. Do not guess another target, downgrade or rerun the customer input to conceal the result. Native cancellation is a request, not rollback; inspect terminal/per-call outcomes. Timeout, termination or lost replies can be indeterminate. Never automatically replay a job, clear edit state or roll back files after an uncertain outcome.
