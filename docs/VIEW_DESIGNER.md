# Studio 5000 View Designer

View Designer creates PanelView 5000 HMI applications. Its project export format is separate from FactoryTalk View ME/SE display XML and Logix Designer L5X. Version 0.6 adds a shared Core workspace, REST API, nine MCP tools and a desktop editor for **exported View Designer projects**.

Use **File > Export Project** in View Designer, then register the resulting folder containing `ViewApplication.hmi` (or that file's absolute path). The folder must be under configured `AllowedRoots`. The service snapshots all files, including images/documents and other assets, and hashes each relative path, byte length and content SHA-256 in an ordered tree manifest. Registration never opens a native `.vpd` or runtime `.vpdr` binary. `ViewDesignerPath` is installation metadata, defaulting to the installed V9 executable; no native executable is launched by these operations.

## Supported editing

The reader exposes nested objects and existing properties from `.hmi` files: screens, popups, Add-On Graphics, navigation shortcuts, data logs, banner/folder settings and other structurally compatible blocks. Existing bindings, events and command parameters are accessible through their property selectors. Unknown catalog types are retained without inventing property semantics. Editing replaces only the selected value span, preserving all other bytes, BOM, UTF-8/UTF-16 encoding, comments and line endings. Other assets are copied unchanged.

Read a file to obtain exact selectors. For example:

```json
{
  "expectedSha256": "<current tree hash>",
  "edits": [
    {
      "file": "User-Defined Screens/Overview.hmi",
      "selector": "/Screen:Overview/TextDisplay:Status/@Text",
      "value": "\"Process ready\""
    },
    {
      "file": "User-Defined Screens/Overview.hmi",
      "selector": "/Screen:Overview/NumericDisplay:ProcessValue/@Value",
      "value": "0 -> \"::LGX.Process.Count\""
    }
  ]
}
```

Values use HMI syntax: quoted strings, numeric literals, booleans or qualified enums, optionally followed by `->` and a quoted binding. The binding text is preserved; the service does not resolve or type-check the controller tag. Use IEC `$` escapes within strings, for example `$"`. Parameter selectors end in `/parameter:Title`; regular properties use `/@Text`. Names and selectors are case insensitive, including caret-prefixed keywords. Duplicate identities/properties are rejected to avoid ambiguous edits, even though native View Designer may take the last property definition.

One preview accepts 1–256 existing-property changes across files. Apply requires its base tree hash, recomputes the result and creates another managed revision. Source exports are untouched; a newly saved native export must be registered again. Refresh after stale-revision errors. New objects/screens, object deletion/renaming, asset replacement, language-file edits and native binary editing are not implemented in this release. Tree limits: 128 MiB, 10,000 files, 64 MiB per asset, 4 MiB per `.hmi`, nesting up to 48 levels. Symbolic links/junctions and ambiguous paths are refused.

## REST and MCP

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/viewdesigner/status` | Installation and evidence boundaries |
| GET/POST | `/api/viewdesigner/projects` | List/register export workspaces (`{"path":"<absolute folder>"}`) |
| GET | `/api/viewdesigner/projects/{id}` | Current project manifest and file inventory |
| GET | `/api/viewdesigner/projects/{id}/file?file=...` | Source, nested object and property selectors |
| POST | `/api/viewdesigner/projects/{id}/previews` | Preview property edits |
| POST | `/api/viewdesigner/projects/{id}/previews/{previewId}/apply` | Apply with `expectedSha256` |
| POST | `/api/viewdesigner/projects/{id}/imports` | Package with `expectedSha256`, `targetProject`, `panelType`, `language` |
| POST | `/api/viewdesigner/imports/{planId}/verify` | Compare a supplied export folder (`reexportPath` optional) |

MCP tools: `viewdesigner_status`, `viewdesigner_list_projects`, `viewdesigner_register_export`, `viewdesigner_inspect_project`, `viewdesigner_read_file`, `viewdesigner_preview_changes`, `viewdesigner_apply_changes`, `viewdesigner_prepare_import`, `viewdesigner_verify_import`. All call the shared service; the adapters contain no independent editing state. The existing `studio_save_artifact` saves a prepared ZIP under `OutputRoots` without overwriting a file. `/api/audit?projectId=...` includes View Designer actions.

Example chat request:

> Register my allowed View Designer export folder, find the Status text on Overview, change it to Process ready, preview and apply, then prepare an import package for Development HMI using the same PanelView model and en-US. Leave native import for me.

## Desktop and native handoff

Open **View Designer…** from the main workspace. Open the export folder, select a file and property, edit its HMI value, preview and apply. Enter the exact native target project, panel type and language, then **Prepare import ZIP**. The handoff tab shows the native import entry path, re-export folder and package ID. **Save ZIP…** saves a transportable package. **Compare re-export** checks the changed properties after a user supplies the actual exported folder containing `ViewApplication.hmi`.

The package contains `import/ViewApplication.hmi` with the complete working export, `original-export/`, and `handoff.json`. Before native import, save a separate VPD backup and confirm the target project, same panel type and language. In View Designer choose **File > Import Project**, select the prepared `ViewApplication.hmi`, inspect messages/screens, then build and save natively. Same-named content is overwritten on native import. Different panel dimensions can rescale imported content. If changing languages, the language file requires a separate native import. Use **File > Export Project** to a fresh folder and supply the folder containing the exported entry file for comparison.

Re-export comparison retains an evidence snapshot. Exact changed values can yield `property_values_match`; this never upgrades `nativeImportStatus` beyond `not_observed_by_service`. Formatting/enum representation differences may require manual review. A matching or copied file does not authenticate its native origin, target project, panel, language, build or runtime behavior. No automated native import, build, download or runtime/device connection is supplied here.

## Validation boundary and references

Development uses generated fixtures in `examples/ViewDesignerDemo`. These are structural development samples, not native-accepted project exports. Unit/integration checks cover nested blocks, parameters, bindings, Unicode/BOM preservation, stale writes, malformed values, preserved assets and simulated re-export comparisons. Native View Designer import, build, save, re-export and panel runtime acceptance must be validated separately on the intended project and installed version; these fixtures do not establish it.

Format/workflow basis: Rockwell's [Studio 5000 View Designer Import/Export Reference Manual, v9.01, 9324-RM001A](https://literature.rockwellautomation.com/idc/groups/literature/documents/rm/9324-rm001_-en-p.pdf), chapters 1–2. The current [Export a View Designer project help](https://www.rockwellautomation.com/en-mde/docs/studio-5000-view-designer/11-01/studio-5000-view-designer-help-ditamap/-st5k--view-designer-help/import-export-vd-project-content/export-a-view-designer-project.html) also documents editable exported files. Installed V8/V9 discovery does not establish that every version accepts generated content; unsupported syntax is reported rather than rewritten.

## Multilingual text

Use Tools > Export Languages for the native KEY/language workbook or Unicode text matrix. The Languages button opens the shared editor for reviewed translations and new language columns. This workflow is separate from ViewApplication.hmi property editing. See [HMI multilingual text](HMI_LANGUAGES.md).
