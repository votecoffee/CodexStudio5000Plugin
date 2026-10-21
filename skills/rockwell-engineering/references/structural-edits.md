# Scoped byte-preserving structural review

Use `studio_structural_edit_context` (read), `studio_structural_edit_preview` (preview), `studio_structural_edit_apply` (apply) and `studio_structural_edit_status` (read) through the matching extension channels. Discover the live generation/schema first. The GUI exposes the same operations under **Diagnostics & workflows → Scoped structural batch**.

General `studio_preview_changes` structural/mixed edits and ordinary XML transactions retain `document-serialization-v1`. This separate workflow uses `structural-byte-spans-v1`: only selected spans change, including on removals. Untouched annotation internal CRLF/CDATA, tag data/definitions, protected libraries, encoding/BOM and other source bytes remain exact. It operates on one pinned managed source and creates a separate **REVIEW ONLY** copy, immutable L5X and receipts. It starts no native job or controller connection.

## Inspect and declare the review

Context requires `request:{projectId,expectedSha256,expectedRevision,xPath?,query?,offset:0,limit:100}` (inspect the live schema for `xpath` casing). Without XPath it pages named tags, routines and UDTs; with XPath it returns one exact element or existing container. Use returned `elementSha256` for each edit/protection. Existing `studio_read_element` supplies component XML; it serializes its response, so use context fingerprints rather than hashing that returned XML as source bytes. Tag context also reports a raw stored-value-set hash.

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
