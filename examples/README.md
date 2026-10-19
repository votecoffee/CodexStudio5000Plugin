# Generated examples

These inputs are small generated development fixtures. They contain no customer project exports, controller addresses or deployment records. File/schema checks do not establish native import, runtime or commissioning acceptance.

| Input | Purpose |
|---|---|
| [Demo.L5X](Demo.L5X) | Logix component inspection, preview/apply and export |
| [ReviewDemo.L5X](ReviewDemo.L5X) | Structural comparison and bracket/keyword ladder previews; generated syntax fixture without native acceptance |
| [ReferencesDemo.L5X](ReferencesDemo.L5X) | PLC where-used candidates, local/controller shadowing, RLL/ST comments, indirect indices and direct aliases; generated syntax without native acceptance |
| [WatchDemo.L5X](WatchDemo.L5X) | v38 tags, UDTs, arrays, offline watch-value decoding and revision-preservation regression checks |
| [WatchTemplateDemo.json](WatchTemplateDemo.json) | Portable watch selections/settings for `WatchDemo.L5X`; no source/controller binding or recorded values |
| [DemoME.xml](DemoME.xml) / [DemoSE.xml](DemoSE.xml) | FactoryTalk graphics properties; matching installed version-17 schemas are needed for mutation |
| [ReferenceDemoME.xml](ReferenceDemoME.xml) / [ReferenceDemoSE.xml](ReferenceDemoSE.xml) | Explicit-context references to `WatchDemo.L5X`: map `PLC` to that snapshot and bind `/gfx[1]` parameter `#1` to `{[PLC]Program:WatchProgram.Record}`; `Other` intentionally remains unmapped |
| [ViewDesignerDemo](ViewDesignerDemo/) | Export-folder parsing, nested properties/bindings, assets and import packaging; native acceptance is unverified |
| [online-test](online-test/README.md) | Program-local arithmetic import with placeholder context and a separate authorized validation procedure |

Use copies for experiments. Keep native projects, real exports, screenshots and per-installation validation reports outside the tracked examples tree, for example under ignored `.local/private-docs/` or `artifacts/`.
