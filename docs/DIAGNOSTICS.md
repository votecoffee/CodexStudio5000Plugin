# Diagnostics, saved trends and compatibility

All operations run through the shared service. The desktop **Diagnostics & workflows…** panel groups the steps and populates the live operation editors with selected project/watch context. Inspect the arguments before running; copy returned IDs and review hashes into later steps. **Watch lists… → Trends & recording…** has dedicated saved-view, graph, statistics and result-export controls.

Discover these operations through `studio_extensions_catalog`, describe the selected operation, then use its indicated read/apply/control channel and current generation. They are extension operations, not additional fixed MCP tools. These host/Core additions require one host update; later compatible extension changes retain the existing reload workflow.

## Saved trends and results

Save a named view with an exact watch revision, selected scalar series (or a structure/array key prefix), and a time window. Creating/modifying/deleting a view does not change polling or recording. Updates and deletion require the loaded view revision; refreshing summaries does not adopt another editor's revision.

In the trend window, choose series and **Save new**. Select a view and **Load**, adjust series/window and **Update loaded**. **Gather results** pins the displayed From/To range and returns numeric statistics, exact first/last JSON values, quality and gaps. **Next result page** advances the bounded recording cursor. Each page's statistics describe that page, not a cumulative aggregate. `hasMore` is explicit; choose a narrower range when complete statistics are needed in one response. Numeric display and aggregates use doubles; original recording/CSV and exact first/last text preserve integer precision.

**Export results JSON…** creates an immutable artifact bound to the analyzed view revision, timestamps, page and result hash. Changed records/settings reject export until results are gathered again. Existing CSV export retains complete selected-range recording rows, including quality and session events. Saving JSON uses configured output roots and preserves existing files. Refreshing the graph clears its result-export selection. Service restart preserves saved views and recordings; watches restart stopped.

| Operation | Effect | REST |
|---|---|---|
| `studio_trend_views`, `studio_trend_view_get` | read | `GET /api/trends`, `/api/trends/{id}` |
| `studio_trend_view_save`, `studio_trend_view_delete` | apply | `POST /api/trends`, `/api/trends/{id}/delete` |
| `studio_trend_analyze` | read | `POST /api/trends/analyze` |
| `studio_trend_export` | apply | `POST /api/trends/export` |

Views have at most 128 distinct selections; analysis limits scalar series to 128 and trend payload to 6 MiB. Use fewer series or smaller pages when limited. Recording retention can remove older samples; a matching revision does not promise complete historical coverage.

Good/missing sample counts exclude session/gap markers. Gaps are reported separately. `seriesLimitReached` indicates an incomplete structure projection even if the recording page has no next cursor.

## Logic tracing

`studio_logic_trace` / `POST /api/projects/{id}/logic/trace` requires the exact saved project hash, tag name, optional program, direction (`upstream`, `downstream`, `both`) and depth 0..8. Program-local declarations shadow controller names. Explicit program-qualified operands and alias chains are resolved; cycles and missing declarations are diagnostics.

The graph reports possible dependencies at base-tag granularity across routines. RLL series/branch guards connect reads to possible writes conservatively. Known operand effects include contacts/comparisons, OTE/OTL/OTU/RES, MOV/NOT, arithmetic/bit operations, COP/CPS/FLL, timers/counters and ONS. Unrecognized effects, expressions, dynamic indices and conditional routine calls remain explicit diagnostics. This does not establish scheduling, scan order, external writers or native compiler binding.

Unambiguous AOI call layouts bind ordered required/visible parameters, follow InOut references and expand available RLL bodies, local tags and parameter aliases. EnableIn/EnableOut are excluded from ordinary call operands. Ambiguous layouts, recursion, protected/unavailable bodies and unsupported languages remain unknown. Rockwell documents parameter usage, ordering and aliases in the [AOI Parameters editor](https://www.rockwellautomation.com/en-us/docs/studio-5000-logix-designer/38-01/contents-ditamap/studio-5000-logix-designer/add-on-instructions/edit-an-add-on-instruction/add-on-instruction-definition-editor---parameters.html).

ST coverage classifies a simple leading single assignment with static operands. Compound statements, calls and block-comment routines have unknown effects. It does not infer IF/loop control dependencies. FBD/SFC execution is not evaluated. Searches and results have explicit source/usage/graph/response budgets; depth-limited results set `limited`.

## Captured rung explanation

1. Create a `studio_diagnostic_capture` using selected saved tag values, or an existing watch sample/record from the exact project/watch revision. This freezes an artifact; it never starts polling. `studio_diagnostic_captures` lists recent metadata.
2. Call `studio_rung_evaluate` with capture ID, capture artifact SHA-256, exact program routine XPath and zero-based rung index.
3. Inspect steps, per-output power, decisive blocking condition IDs, unknown condition IDs and diagnostics.

Evaluation uses the capture's historical project revision, even after the working project changes. Series conditions use AND, branch legs use OR, with true/false/unknown propagation. XIC/XIO, EQU/NEQ/GRT/GEQ/LES/LEQ/LIM, AFI and NOP are supported; OTE reports rung power. Known-false conditions may still block a path containing independent unknowns. Numeric/BOOL values, static structure/array members and bit references can be selected from captures.

Unavailable/bad-quality values, expressions, stateful instructions, earlier writes and unknown instruction effects cannot become assumed execution results. Parse errors return unknown with no invented outputs. Timer/counter transitions, AOI execution, FBD/SFC, latches, forces, scheduling and competing writers are not simulated. Rockwell's [OTE reference](https://www.rockwellautomation.com/en-hu/docs/studio-5000-logix-designer/38-02/contents-ditamap/instruction-set/bit-instructions1/output-energize--ote-11.html) describes its rung-condition behavior; a predicted OTE condition does not establish the final physical output state.

Saved values are labeled snapshots. Watch samples preserve acquisition start/completion times and quality; they are not simultaneous controller snapshots. The source hash identifies the selected saved declarations; it does not authenticate the controller's running logic revision. Capture/evaluation perform no tag writes.

REST: `GET/POST /api/diagnostics/captures`, `POST /api/diagnostics/evaluate`. Creation uses apply; listing/evaluation use read.

## Triggered recording

Save a `studio_diagnostic_trigger_save` definition binding a watch revision to the exact managed project hash/revision. Conditions are `change`, scalar `predicate`, observed predicate `timeout`, or watch communication `fault`. A PLC fault bit is a predicate on its selected watch key; `fault` denotes reader/communication failure. Timeout requires the predicate to remain observed true over its hold interval. A sample gap, session change, nonmonotonic time or unavailable value resets continuity.

Arming via `studio_diagnostic_trigger_arm` prepares local recording only. Start the authorized watch separately. Captures retain a ring of up to 256 pre-samples and up to 256 post-samples. A trigger fires once; rearm explicitly for another episode. Normal ring rotation is expected; byte-budget losses are counted. Pre-buffer budget is 4 MiB and each episode is limited to 8 MiB. Stop/fault/restart during the post-window produces an interrupted episode with explicit diagnostics. Startup recovers pending windows and leaves triggers disarmed.

`studio_diagnostic_triggers` returns status and retained episode summaries. `studio_diagnostic_episode` reads the immutable artifact. `studio_diagnostic_episode_capture` freezes a chosen frame for rung explanation; the default is the trigger sample. A fault-only event has no invented sample: select an earlier sampled frame explicitly. Retained episode counts are 1..20 per definition; removing old summary entries or deleting a disarmed definition preserves separate immutable artifacts. This bounds the active episode index, not total artifact disk usage.

Use the loaded trigger revision for updates/deletion. Armed/capturing definitions cannot be edited. A changed watch or source binding requires updating the definition before arming. Closing a viewer does not stop an existing shared watch.

REST: `GET/POST /api/diagnostics/triggers`, `POST /api/diagnostics/triggers/{id}/arm`, `.../{id}/delete`, `GET .../{id}/episodes/{episodeId}`, `POST .../{id}/episodes/{episodeId}/capture`. Listing/episode reads use read; local definition, arm and capture changes use apply. Starting the watch remains a separate control operation under existing controller policy.

## Logic and saved-value merging

Select three independently pinned managed snapshots: common baseline, modified logic/configuration, fresh saved values. An authorized upload can first create the fresh snapshot through the existing capture workflow. This merge itself does not connect.

`studio_logic_value_merge_preview` reports per-tag baseline/logic/fresh value hashes and explicit conflicts. Modified logic/configuration is preserved. If fresh values match baseline, preserve modified values; if modified values match baseline, validated fresh values can be copied. Differing edits on both sides conflict. A new/removed tag or divergent fresh logic/configuration requires explicit review. Fresh definitions differing from both baseline and modified logic block apply.

Resolve a value conflict per destination using `logic` or `fresh`, then preview again. Choosing fresh still requires supported, complete, compatible declarations and decorated values; it cannot bypass schema, alias or safety restrictions. Choosing logic explicitly preserves its value/removal. Whole-tag maps can select/rename source/destination scopes, with at most 256 unique destinations. For a larger project select a bounded subset. Removed-tag conflict reporting is bounded separately.

`studio_logic_value_merge_apply` requires the exact preview review hash and all current input hashes. It creates a new managed project, preserving the three originals. Export a separate L5X/ACD through existing operations. No automatic native build/runtime acceptance is claimed.

REST: `POST /api/logic-value-merge/preview` (read), `/api/logic-value-merge/apply` (apply).

## Version compatibility

`studio_version_compatibility_preview` plans an offline copy for target major revisions 31..38. It rejects unknown/inconsistent source revisions and downgrades; it never rewrites headers to manufacture compatibility. The [Logix Designer SDK Getting Results Guide](https://literature.rockwellautomation.com/idc/groups/literature/documents/gr/ldsdk-gr001_-en-p.pdf) documents ConvertAsync as an upgrade path.

`studio_version_compatibility_start` requires the reviewed plan hash. The shared SDK layer snapshots inputs, calls ConvertAsync for upgrades, BuildAsync where supported (v37+), and SaveAsync. The native bridge publishes separate ACD/L5X artifacts. Native target installation, licensing and project support still apply. No controller methods are generated.

After successful conversion, `studio_version_compatibility_readback` starts a separate job that opens the saved ACD artifact and produces a fresh L5X. Repeated readback requests reuse the recorded job. `studio_version_compatibility_status` distinguishes conversion and readback jobs. Only successful, source/artifact-bound readback produces a report: expected software/controller revisions, XML configuration changes, and observed native unsupported/default messages. Expected major-version fields are excluded from configuration differences. Absent XML alone does not prove unsupported/defaulted behavior; inspect native diagnostics and the resulting project.

REST: `POST /api/version-compatibility/preview`, `/api/version-compatibility/start`, `/api/version-compatibility/{id}/readback`; `GET /api/version-compatibility/{id}`. Preview/status use read; native start/readback use control. This release's automated checks cover plans, rejection paths and generated readback comparisons. The new end-to-end native conversion/readback workflow requires separate installed-version validation; no controller or native engine is invoked by isolated checks.
