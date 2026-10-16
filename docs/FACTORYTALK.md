# FactoryTalk View Studio ME and SE

Version 0.2 adds existing graphics-property editing to the same service, API, MCP adapter and desktop GUI as Logix. Export current display XML from Studio, register it, inspect named objects and connections, preview/apply edits, prepare an update import, perform the native wizard phase, and verify a fresh export. The plugin includes a `factorytalk-studio` skill for that native phase when Computer Use tools are available.

The installed Rockwell graphics schema named by the XML is used for validation. Schema filenames are restricted to ME/SE local names; paths/URLs, DTDs and remote resolution are rejected. Schema-valid XML does not prove application references, native import, runtime rendering, compile results or deployment. A missing schema permits structural inspection but prevents mutation and import preparation. An invalid file against an installed matching schema is rejected.

Supported changes are existing captions, state captions, connection/animation expressions, colors, geometry and other existing attributes/text values. `replace_element` can change existing values while preserving identity, attribute names and child structure. Object creation/deletion and reference-aware renaming require a separate native workflow. Do not hand-edit `.gfx`, `.apa` or `.mer` files.

## Native application interaction

`ftview_status` reports the configured launcher, installed schemas and Studio windows. `ftview_launch_studio` opens the installed launcher. Select the correct ME or SE application type through the UI; for SE, identify the intended topology and HMI server. This release does not guess application launch switches or use an undocumented Studio COM editing API.

The shared import plan binds the working hash to explicit application, display and current language. Its batch file names exactly one target display with `importType="update"`; it never uses the wildcard display target. Native graphics backups and the preserved original export have separate paths and meanings. Close the target display before import and avoid discarding unrelated unsaved work. The bundled skill follows Studio's observed Graphics Import Export Wizard and checks its completion/error log. If an import is interrupted, re-export and reconcile the actual state before retrying because partial changes may remain.

The graphics wizard operates on the current application language. Export and import in the same confirmed language. This workflow does not merge multilingual strings. Native import changes the Studio project only; it does not deploy to a PanelView, SE client or runtime server.

Re-export the exact target display to the plan's path. The verification tool validates that XML and compares only the intended changed properties using stable named selectors. It records the supplied bytes and hash. Matching file values do not authenticate the application, language or native source; observe the UI context and wizard completion as well. The service always labels native import as unobserved by the service.

## Validation in this development session

Both authored ME and SE examples validated against the installed `Gfx-ME17.xsd` / `Gfx-SE17.xsd`, and each completed caption plus connection editing through real REST/MCP calls. Copied expected XML served as simulated re-export input to prove that matching file values **do not** upgrade native acceptance. Core tests also cover mismatches, missing evidence, schema errors, preserved identities and source bytes, and reordered native objects.

The supplied screen-export ZIP contained 60 graphics XML files and two batch XML files. **59 graphics files validated; one was rejected.** `FTViewME/Displays/999 Load ActiveX.xml` contains two connection names `Memory (BinFS) Thrashing` that violate `connectionNameType` in its declared installed schema. The exporter/native reason for that inconsistency is unresolved. The service retains the rejection instead of editing the archived source or vendor schema. The two batch files are not display XML inputs. Corpus validation exits nonzero when any graphics input fails and writes its complete report.

Studio was launched and its application-type selection was observed. The user stopped Computer Use with Escape before an ME/SE application was opened. Native wizard import, save/re-export and runtime acceptance therefore remain **pending** for both editions. No native HMI project was modified in this session. The new desktop application was independently rendered with both examples; that checks its own layout, not Studio's native wizard.

Official workflow references are included in [the bundled skill reference](../skills/factorytalk-studio/references/native-workflow.md).
