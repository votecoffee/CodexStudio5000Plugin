# HMI multilingual text

FactoryTalk View **ME and SE** and Studio 5000 **View Designer** language exports have a shared offline workspace behind REST, MCP extensions and the desktop GUI. This is separate from graphics XML and `.hmi` property editing: a caption in one display export does not represent the application's multilingual database.

## Supported files

| Export | Recognition and editable content |
| --- | --- |
| FactoryTalk all-language workbook | OpenXML ZIP signature, even when Studio names it `.xls`. Native headers are `Server`, `Component Type`, `Component Name`, `Description`, then language-code columns. The four identity columns are immutable. |
| FactoryTalk selected-language Unicode `.txt` | Seven native header lines are preserved verbatim. Body columns are component type, component name, string reference and string definition. Supply the selected language explicitly when registering; the service does not infer or relabel the native header. |
| View Designer `.xlsx` | OpenXML workbook with immutable `KEY` source-string identities and language columns such as `en-US` and `es-ES`. Header context following a space is retained. |
| View Designer Unicode `.txt` | Tab-delimited `KEY`/language matrix, including quoted tabs, doubled quotes and multiline fields. |

UTF-8 and UTF-16 LE/BE text is supported, preserving the BOM, encoding and untouched text spans. FactoryTalk text uses its native backslash/newline syntax; the API exposes those export strings without interpreting or replacing their escapes. Standard Excel-style doubled quote escaping is supported. Ambiguous Notepad quoting is rejected; normalize a separate Unicode text copy through the documented Studio/Excel workflow.

The file signature determines workbook recognition. Legacy OLE/BIFF binary `.xls`, SpreadsheetML XML, signed/macro workbooks, ambiguous duplicate identities/language headers, unknown column layouts and unsupported worksheet relationships are rejected. Save a separate OpenXML workbook or use a supported native Unicode export. Microsoft Office and a spreadsheet package dependency are not required.

Existing workbook styles, metadata and unknown package parts are retained; only changed worksheet XML is serialized. Untouched ZIP entry payloads remain byte-for-byte identical. Formula, typed numeric/boolean/error, rich text and metadata-bearing target cells are read-only. Protected or merged tables are read-only. New language columns contain explicit edited values or empty strings; they do not copy/fallback to another language. No API deletes language columns or changes native keys.

## Desktop workflow

1. In Studio, export the application's language strings. Preserve an original backup. In View Designer, use **Tools > Export Languages**, separately from the `.hmi` project export.
2. Choose **HMI languages…** in the main GUI, or **Languages…** in the View Designer window. Select `ftview-me`, `ftview-se` or `view-designer`. For a selected-language FactoryTalk text file, enter its native selected language before opening it.
3. Open the language export. Search native identities and translations, and select a string/language row. Enter translated text and **Stage text**. To add a language, enter its code and **Stage language column**; stage text for that code on the relevant native keys.
4. **Preview translations** shows exact before/after strings, added languages, source hash and result/review hashes. **Apply reviewed translations** creates a managed revision. The original file is never overwritten. Drafts survive paging and a concurrent source-change rejection; discard/reload explicitly to adopt a changed revision.
5. **Export native language file…** saves a separate `.xls`, `.xlsx` or `.txt` file in the same container as the source. Apply/discard staged text before exporting. Existing output files are preserved.
6. Import the new file through the intended native Studio language workflow. In View Designer retain all existing language columns: its import can remove languages omitted from a file. Inspect native warnings and confirm the intended target application/languages.
7. Export fresh native language strings and use **Compare Studio re-export…**. This compares every native identity and language string, including missing/extra keys or columns. A matching file does not authenticate provenance, native import success, font/layout correctness or runtime language switching.

Translation text is explicit user/API input. Keep embedded references, placeholders, formatting and escape conventions correct for the target application; the service does not interpret their application-specific semantics or generate translations. Check translated layout, fonts and runtime behavior in the native tools.

## REST and MCP

`POST /api/hmi-languages` accepts `{ "path": "<allowed absolute native export path>", "platform": "ftview-me", "language": null }`. `language` is required only for single-language FactoryTalk text. `GET /api/hmi-languages` lists workspaces and `GET /api/hmi-languages/{id}` reads the current revision/hash. The explicit platform is caller-selected context; language-file contents do not authenticate a particular application or Studio edition.

Each workspace provides these endpoints:

Input and supplied re-export paths must be inside configured `AllowedRoots`; native-file save paths must be inside `OutputRoots`. An output directory is not implicitly an allowed source directory. Configure the intended input directories in local settings before opening native exports.

| POST suffix | Request and result |
| --- | --- |
| `/browse` | `expectedSha256`, optional `query`, `language`, `offset`, `limit`; returns native context and every language string, with cell writability. The language filter limits text searched; all translations remain visible. |
| `/preview` | Exact source hash, explicit edits and optional `addLanguages`; returns review/result hashes and before/after changes without modifying state. |
| `/apply` | `{ "preview": <original preview request>, "expectedReviewSha256": "<review hash>" }`; recomputes and verifies the whole batch, then creates one managed revision. |
| `/export` | `{ "expectedSha256": "<current hash>" }`; returns a native-file artifact. Save with `studio_save_artifact` or the guarded artifact-save REST endpoint. |
| `/verify` | `{ "expectedSha256": "<current hash>", "path": "<allowed fresh export path>" }`; reports content agreement and bounded differences. This is stateless, with no native calls. |

Example preview request:

```json
{
  "expectedSha256": "<current workspace hash>",
  "addLanguages": ["fr-FR"],
  "edits": [
    { "key": "<key from browse>", "language": "fr-FR", "text": "Prêt" },
    { "key": "<key from browse>", "language": "zh-CN", "text": "就绪" }
  ]
}
```

The `factorytalk` extension exposes `ftview_languages_{list,get,register,browse,preview,apply,export,verify}`. The `viewdesigner` extension exposes the same operations with prefix `viewdesigner_languages_`. Both call the same shared service. Prefixes aid discovery; `platform` in the register request remains explicit. Describe the operation to obtain its current schema/generation. Register/apply/export use `studio_extensions_apply`; list/get/browse/preview/verify use `studio_extensions_read`. Workspace operations use `arguments: { workspaceId, request }`; register uses `{ request }` and get uses `{ workspaceId }`.

All existing language columns, identities and unedited translations are independently read back before apply. A stale source, altered review or one invalid edit rejects the batch. Limits: 16 MiB source/output, 64 MiB expanded workbook, 512 ZIP entries, 50000 records, 64 languages, 200 edits per batch, at most 16 added languages, 32767 UTF-16 characters per text, browse pages of 1..500 records/4 MiB and previews/difference responses up to 4 MiB. Differences report the total count and first 200 details.

## Validation and native boundaries

Generated workbook/text fixtures cover ME/SE identities, View Designer keys, Unicode/Excel escapes, source preservation, unselected translations, new language columns, unsupported shapes, stale reviews and shared REST/MCP/GUI operations. Rebuild public fixtures with `py -3 scripts/New-HmiLanguageExamples.py`. They contain synthetic data and are not native acceptance evidence. Real View Designer import/build/runtime validation remains pending.

An optional local smoke command validates an actual workbook using a private copy, changes one copied translation, exports/reopens it and checks source hashes and untouched package entries. It uses Core directly with controller operations disabled and no service/native bridge:

```powershell
dotnet run --project tests/Studio5000.Integration -c Release -- --hmi-language-offline '<absolute workbook path>' ftview-me
```

Results and copied files stay under ignored `artifacts/hmi-language-validation/<id>/`. Do not commit customer exports, native import logs or local validation reports. The command currently targets matrix workbook exports; single-language FactoryTalk text registration requires the normal API's explicit language context.

Native format/workflow references: [FactoryTalk View ME manual, language switching](https://literature.rockwellautomation.com/idc/groups/literature/documents/um/viewme-um004_-en-e.pdf), [FactoryTalk View SE manual](https://literature.rockwellautomation.com/idc/groups/literature/documents/um/viewse-um006_-en-e.pdf), [View Designer language export](https://www.rockwellautomation.com/en-mde/docs/studio-5000-view-designer/10-00/studio-5000-view-designer-help-ditamap/-st5k--view-designer-help/switch-languages-on-the-hmi-device/export-text-strings-for-language-translation.html), [View Designer language import](https://www.rockwellautomation.com/en-il/docs/studio-5000-view-designer/10-00/studio-5000-view-designer-help-ditamap/-st5k--view-designer-help/switch-languages-on-the-hmi-device/import-a-language-file.html).
