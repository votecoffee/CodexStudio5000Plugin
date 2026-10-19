# Community integration candidates

Prefer focused dependencies or isolated adapters over importing whole applications. Keep domain operations in Core/the shared service, expose additions through the stable extension catalog, pin any adopted source/package revision, retain licenses and verify with generated fixtures before native use.

The following are candidates, not installed or validated dependencies unless explicitly described below.

| Project | License | Candidate capability | Adoption considerations |
|---|---|---|---|
| [Rockwell custom tools](https://github.com/RockwellAutomation/ra-logix-designer-vcs-custom-tools) | MIT | Exploded-format versioning, ST metadata preservation, dependency ordering and Git workflows | The archived library is already vendored. Review newer changes selectively; current upstream targets .NET 10. |
| [L5Sharp](https://github.com/tnunnink/L5Sharp) | MIT | Typed Logix components, queries and generation | A .NET fit; test preservation and supported XML dialects before replacing existing editing paths. |
| [Logix Workbench](https://github.com/ChrisMangin/logix-workbench) | MIT | Graphical ladder rendering, rung-level diff and AOI forms | Evaluate the frontend renderer separately from the Rust backend. Keep parsing/diff results in the shared service. |
| [FactoryTalk XML tag extractor](https://github.com/dergroncki/parse_ft_gfx_xml) | MIT | Display tag references and a basis for PLC/HMI cross-references | A focused Rust implementation; unresolved parameter and shortcut references need explicit representation. |
| [libplctag.NET](https://github.com/libplctag/libplctag.NET) | MPL-2.0 | Optional live tag access for polling/recording | Preserve its separate license and native dependency notices. It does not replace native project editing APIs. |
| [LogixDriver](https://github.com/bstiffler582/LogixDriver) | MPL-2.0 | Live tag browsing, type discovery/cache and structured reads | Evaluate behind the existing controller authorization, scope and watch abstractions. |
| [L5Sharp.Gateway](https://github.com/tnunnink/L5Sharp/tree/main/src/L5Sharp.Gateway) | MIT; libplctag dependency separately licensed | Map live values to typed structures; virtual tag service for tests | Validate packing, version compatibility and polling behavior independently. |
| [pycomm3](https://github.com/ottowayi/pycomm3) | MIT | Reference for tag discovery, UDT decoding and protocol tests | Upstream states it is no longer actively developed. Avoid making it a required runtime dependency. |

## Adoption sequence

1. **Exploded compatibility:** implemented shared-service schema checks and explicit rejection of known unsupported metadata/encoded AOIs. See [EXPLODED_PROJECTS.md](EXPLODED_PROJECTS.md). The existing vendor source remains unchanged.
2. **Project comparison and ladder preview:** implemented hash-bound structural comparison, occurrence alignment, bounded branch parsing, passive SVG and desktop viewers. See [PROJECT_REVIEW.md](PROJECT_REVIEW.md). Generic AOI blocks and structural differences do not establish native instruction semantics or runtime behavior.
3. **PLC/HMI references:** FactoryTalk extraction and explicit-context resolution are implemented in the shared service, REST, extension catalog and desktop. Scoped literal parameters and shortcut mappings resolve against hash-bound saved Logix tags, UDT members, arrays and integer bits. Unknown context/templates and snapshot absence remain distinct; no PLC connection occurs. See [FACTORYTALK.md](FACTORYTALK.md#plchmi-reference-resolution).
4. **Alternative live-data backend:** benchmark an optional read-oriented adapter with generated/offline tests first, then an explicitly authorized controller fixture. Native project editing remains on the Rockwell SDK.

Review licenses of the exact files and transitive dependencies before copying or distributing code. Keep public documentation reusable; private research checkouts, native projects and per-installation validation receipts belong in ignored local folders.

The FactoryTalk scanner was informed by [extractor revision 6ba6466b2bdecee6caf2812d5825e7f0c174c0f4](https://github.com/dergroncki/parse_ft_gfx_xml/tree/6ba6466b2bdecee6caf2812d5825e7f0c174c0f4), which collects connection expressions and substitutes reference-object parameters. The C# scanner and resolver are new implementations with hash-bound snapshots, pagination, exact scoped context and conservative unresolved labels; no Rust source, executable or dependency was copied into this repository. Parameter expansion requires explicit caller-supplied scopes/values and does not infer native instance context from export values.
