# FactoryTalk View Studio ME and SE

Version 0.2 adds existing graphics-property editing to the same service, API, MCP adapter and desktop GUI as Logix. Export current display XML from Studio, register it, inspect named objects and connections, preview/apply edits, prepare an update import, perform the native wizard phase, and verify a fresh export. The plugin includes a `factorytalk-studio` skill for that native phase when Computer Use tools are available.

The installed Rockwell graphics schema named by the XML is used for validation. Schema filenames are restricted to ME/SE local names; paths/URLs, DTDs and remote resolution are rejected. Schema-valid XML does not prove application references, native import, runtime rendering, compile results or deployment. A missing schema permits structural inspection but prevents mutation and import preparation. An invalid file against an installed matching schema is rejected.

Supported changes are existing captions, state captions, connection/animation expressions, colors, geometry and other existing attributes/text values. `replace_element` can change existing values while preserving identity, attribute names and child structure. Object creation/deletion and reference-aware renaming require a separate native workflow. Do not hand-edit `.gfx`, `.apa` or `.mer` files.

## Tag references

The shared service extracts reference occurrences from `expression` attributes and global-object `parameter/@value` attributes. It retains each source expression, its exact property XPath, owning object, character offset/length and the tag/shortcut text. Repeated uses remain separate occurrences. The scan supports braced references and simple standalone ASCII HMI names or parameter tokens; quoted string literals are skipped. It does not compile the full FactoryTalk expression language or scan VBA, command strings, embedded caption variables or an HMI tag database.

Use **Tag references** in the desktop to view the first page. For search/pagination, use **Extensions… → factorytalk → ftview_tag_references**, or discover/describe that operation through `studio_extensions_*` and invoke it with `studio_extensions_read`. The fixed MCP tool list stays unchanged.

REST uses `POST /api/projects/{id}/graphics-references`:

```json
{
  "expectedSha256": "CURRENT_DISPLAY_SHA256",
  "query": "CycleCount",
  "offset": 0,
  "limit": 200
}
```

Extension arguments are `{ "projectId": "DISPLAY_ID", "request": { ...the body above... } }`. The hash binds every page to a managed revision. A changed revision produces `stale_revision`; refresh its hash before continuing. `query` is a case-insensitive substring of reference text or object name. `total` counts filtered occurrences before pagination.

Resolution labels describe missing context, not missing tags: direct references require a shortcut mapping, HMI references require the HMI database, and `#` references require parameter context. No controller connection or automatic parameter substitution occurs. Malformed/unsupported expressions produce bounded diagnostics; this is a conservative inventory, not a complete reference or binding-validity audit. Read-only extraction needs no installed XSD, and source bytes/revisions remain unchanged.

Limits: 200 rows per page, 256 query characters, 16 KiB per expression, 10000 reference occurrences per display, 8192 characters per selector, 50 diagnostic entries and a 4 MiB encoded response. Oversized pages return `reference_page_too_large`; use a smaller limit. Invalid paging returns `invalid_reference_query`; scan limits return `reference_limit`.

## PLC/HMI reference resolution

`POST /api/projects/{id}/graphics-reference-resolution` and the `factorytalk` read operation `ftview_resolve_references` use explicit context to check extracted references against registered Logix snapshots. The shared service preserves the original occurrence and expression, expanded reference, parameter trace, target project/hash, resolved program/tag, selected type and remaining array dimensions. These reads preserve source files and managed revisions and never connect to a controller.

In the desktop, open **Resolve references…** and select a registered ME/SE display. Resolve initially without context to see occurrences and available exact scopes. On **PLC shortcuts**, enter a shortcut name, select a registered Logix snapshot and set the mapping. On **Scoped parameters**, choose/paste the exact scope, enter `#number` and its literal value, and set the binding. Resolve again to update the results. Select a row for source selectors, hashes, expansion trace and diagnostics. Search and Previous/Next page the results; removal buttons remove the selected context entry. Changing display or refreshing snapshots clears context so old scopes/hashes cannot carry over silently.

Select a matched PLC reference and use **Where used in PLC…** to inspect that base tag's saved RLL/ST candidates and direct alias declarations. The linked view retains the mapped PLC hash and rejects changed snapshots. See [PLC where-used](TAG_REFERENCES.md) for lexical coverage and ladder navigation.

```json
{
  "expectedSha256": "CURRENT_DISPLAY_SHA256",
  "shortcuts": [
    { "shortcut": "PLC", "projectId": "LOGIX_PROJECT_ID", "expectedSha256": "CURRENT_LOGIX_SHA256" }
  ],
  "parameters": [
    { "scopeXPath": "/gfx[1]/group[@name='Mixer']", "parameter": "#1", "value": "{[PLC]Program:Example.Record}" }
  ],
  "query": null,
  "offset": 0,
  "limit": 100
}
```

Both display and mapped PLC hashes are required. Scope selectors must be exact canonical object/group selectors returned by extraction or the resolution report's `parameterScopes`; `/gfx[1]` explicitly applies a display-wide binding. The nearest supplied ancestor binding wins. Two groups can bind `#1` differently. Parameter values are literal fragments, optionally wrapped in braces, and may reference other supplied parameters. Exported `parameter/@value` attributes are inventoried but never silently assumed to be the reference instance's parameter context. This is an explicit context evaluator, not a model of every native parameter-file/global-object rule. Rockwell documents [global parameter values per reference object](https://www.rockwellautomation.com/en-us/docs/factorytalk-view/16-00-00/me-help-ditamap/global-objects-and-object-displays/global-object-parameters.html).

Tag, program, member and shortcut names compare case-insensitively. Supported paths include controller tags, `Program:Name.Tag`, declared UDT members, constant one-to-three-dimensional array indices such as `[1,2]`, and integer bit selectors such as `.31`. Whole arrays retain their declared dimensions. The scanner's coverage and diagnostics above still apply.

`matched_in_snapshot` means the selected address exists in saved metadata; it does not establish runtime connectivity, external access or expression validity. `tag_absent_in_snapshot`, `program_absent_in_snapshot` and `member_absent_in_snapshot` mean absence only in the selected file, which may be a partial export. Missing shortcut/parameter/HMI database context, missing built-in/AOI/module templates, aliases and dynamic addresses remain explicit unresolved statuses. Array rank, bounds and integer bit failures have separate statuses. No native Studio acceptance is implied.

`query` filters original/expanded references, owning object names or status; `total` counts matching occurrences before paging. `parameterScopes` lists exact available ancestors for the returned page. Limits: 64 unique shortcuts, 256 unique scope/parameter pairs, 1024 characters per parameter value, 16 recursive levels, 64 expansions per occurrence, 4096 expanded characters, 200 rows and 4 MiB per response. Extraction limits also apply. Context errors return `invalid_resolution_context` or `parameter_scope_missing`; stale hashes return `stale_revision`. Cycles and expansion bounds are per-occurrence statuses.

## Native application interaction

`ftview_status` reports the configured launcher, installed schemas and Studio windows. `ftview_launch_studio` opens the installed launcher. Select the correct ME or SE application type through the UI; for SE, identify the intended topology and HMI server. This release does not guess application launch switches or use an undocumented Studio COM editing API.

The shared import plan binds the working hash to explicit application, display and current language. Its batch file names exactly one target display with `importType="update"`; it never uses the wildcard display target. Native graphics backups and the preserved original export have separate paths and meanings. Close the target display before import and avoid discarding unrelated unsaved work. The bundled skill follows Studio's observed Graphics Import Export Wizard and checks its completion/error log. If an import is interrupted, re-export and reconcile the actual state before retrying because partial changes may remain.

The graphics wizard operates on the current application language. Export and import in the same confirmed language. This workflow does not merge multilingual strings. Native import changes the Studio project only; it does not deploy to a PanelView, SE client or runtime server.

Re-export the exact target display to the plan's path. The verification tool validates that XML and compares only the intended changed properties using stable named selectors. It records the supplied bytes and hash. Matching file values do not authenticate the application, language or native source; observe the UI context and wizard completion as well. The service always labels native import as unobserved by the service.

## Validation

Generated ME and SE examples in `examples/` target `Gfx-ME17.xsd` / `Gfx-SE17.xsd`. Automated checks exercise caption/connection editing through REST/MCP when matching schemas are installed. Simulated re-export checks verify that matching file values do not upgrade native acceptance. Core tests also cover mismatches, missing evidence, schema errors, preserved identities/source bytes and reordered native objects.

To check a private export corpus, run the integration verifier's `--factorytalk-corpus <zip-path>` mode against allowed local inputs. Its report records archive/input/schema hashes and per-file results under ignored `artifacts/test-results/factorytalk-corpus.json`. Batch XML is not display XML. Corpus validation exits nonzero when a graphics input fails; retain the failure and investigate without modifying the source archive or vendor schema.

Schema validation and desktop renders do not establish native wizard import, save/re-export or runtime acceptance for either edition. Validate those steps on the intended native project copy and record application, edition, language and observed outcomes in private local notes. See [VALIDATION.md](VALIDATION.md).

Official workflow references are included in [the bundled skill reference](../workflows/factorytalk-studio/references/native-workflow.md).

## Multilingual text

ME/SE language exports use their own preserved workbook/Unicode workspace, separate from display XML. The GUI and reloadable extension support reviewed per-language edits, new columns, native-file export and supplied re-export comparison. See [HMI multilingual text](HMI_LANGUAGES.md).
