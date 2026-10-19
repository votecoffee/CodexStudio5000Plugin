# Exploded-project compatibility

RockwellMCPServer preserves the archived L5xploder library and wraps it with shared-service checks. Its layout is **schema 1**. An exploded ZIP is an interchange format for review and version control; it is not native Studio validation.

## Supported inputs and outputs

`studio_export_project` with format `explode` writes `schema_version: 1` into `RSLogix5000Content/export-options.yaml`. Recomposition accepts that version and legacy directories without a version stamp. The original source directory is copied into private staging and is never rewritten.

Newer upstream schema 2 retains structured-text routine XML alongside `.st` files. The preserved reader cannot reconstruct that layout. Import rejects any other stamped version before reading its XML or publishing a managed project. Do not remove/lower another tool's stamp to bypass this check; use the matching tool to produce an L5X, then register that L5X.

Options must be one flat YAML mapping with unique scalar keys/values, at most 64 KiB. Supported settings are `schema_version`, `serialization_format: Xml`, `xml_attribute_per_line` and `omit_export_date`. Booleans must be `true`/`false`. `unsafe_skip_dependency_check: false` is tolerated; `true` is rejected. Unknown settings and malformed YAML fail explicitly.

## Content boundaries

YAML aliases and nested collections in export options are rejected before loading the mapping.

- Protected/encoded Add-On Instructions are rejected on exploded export and import. The legacy sorter does not consume newer dependency metadata, so the presence of a `Dependencies` export option does not make this reader compatible. Keep these projects in L5X/ACD form or use a dependency-aware exploded tool.
- Schema 1 persists ST code but omits routine descriptions and other XML metadata. Exploding ST routines with extra attributes/elements or XML comments is rejected to prevent that known loss. Ordinary L5X export preserves the managed revision bytes.
- ST logic without those extra fields, ordinary ladder content and the existing legacy options remain supported. General cross-reference/dependency correctness still requires native verification.

| Error code | Meaning |
|---|---|
| `unsupported_exploded_schema` | Version stamp is unsupported or invalid |
| `invalid_exploded_options` | Ambiguous, oversized, unknown or unsupported options |
| `unsupported_exploded_aoi` | Protected AOI ordering cannot be handled by the legacy reader |
| `unsupported_exploded_metadata` | ST metadata would be lost in schema 1 |

These are HTTP 400 operation errors; MCP reports the same service failure. A rejected operation does not alter the managed revision or original source. See [VALIDATION.md](VALIDATION.md) for the offline verification procedure.

## Upstream basis

The compatibility review used [Rockwell upstream revision 923a9675a53637515c8a5088ff7eed41a9b3eb82](https://github.com/RockwellAutomation/ra-logix-designer-vcs-custom-tools/tree/923a9675a53637515c8a5088ff7eed41a9b3eb82), including `ExplodedSchemaVersion`, ST serialization and AOI dependency ordering. The shared-service guards are implemented locally; the 24 archived vendored files and their provenance hashes remain unchanged. This does not import upstream's .NET 10 toolchain, SDK 2.2 bindings or complete schema-2 support.
