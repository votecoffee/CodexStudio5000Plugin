# Third-party provenance

`third_party/L5xploderLib` was copied from `src/L5xploderLib` in the `ra-logix-designer-vcs-custom-tools-main` source distribution.

The source distribution's license is MIT, copyright (c) 2025 Rockwell Automation. Its full text is preserved in [third_party/ROCKWELL-LICENSE.txt](third_party/ROCKWELL-LICENSE.txt). Archive and source hashes are recorded in [docs/upstream-manifest.json](docs/upstream-manifest.json). The manifest identifies the distribution without recording a contributor's local filesystem paths. Any changes to the copied source must be recorded in that manifest.

The optional `Studio5000.NativeBridge` uses RockwellAutomation.LogixDesigner.CSClient 2.1.974 from the user's installed local SDK feed. Its proprietary binaries and FactoryTalk helper are only local build artifacts and are excluded from Git. This notice does not grant redistribution rights to SDK binaries. The bridge explicitly includes the SDK's FtspAdapter.exe in its local publish output because the package's build-only copy target does not include it in `dotnet publish`.

The project also restores YamlDotNet, the official ModelContextProtocol C# SDK, Microsoft .NET libraries, and test dependencies from NuGet. Exact dependency versions are pinned in package lock files; see each package's license. No proprietary archive sample PLC projects are committed as examples.

The optional Python app host depends on the MIT-licensed [universal-codex-tools framework](https://github.com/votecoffee/CodexMCPBroker), pinned to 0.20.0 with MCP Python SDK 2.3.0 and a separate runtime lock. It is installed from a verified wheel, not vendored here. The distribution/import names provide dependency compatibility; each app retains its own plugin and native skills. See [framework packaging and provenance](docs/FRAMEWORK.md).

## Optional libplctag watch adapter

Only `Studio5000.CipBridge` restores these additional packages; neither their source nor native binaries are vendored into the MIT application source:

| Package | Version | License | Corresponding upstream source |
|---|---|---|---|
| libplctag | 1.5.2 | MPL-2.0 | [libplctag.NET revision 343d1b0edeb7fcbae5d56e81477af7b3dc5b05fa](https://github.com/libplctag/libplctag.NET/tree/343d1b0edeb7fcbae5d56e81477af7b3dc5b05fa) |
| libplctag.NativeImport | 1.0.41 | MPL-2.0 | [libplctag.NET revision 6ba1b192553372e65fb10eb6a0f1fb577890bdb9](https://github.com/libplctag/libplctag.NET/tree/6ba1b192553372e65fb10eb6a0f1fb577890bdb9) |

NuGet content hashes are preserved in [the worker lock file](src/Studio5000.CipBridge/packages.lock.json). NativeImport bundles the native libplctag component; the Windows library reports 2.6.0 in the offline load check. Native source and its license notices are available at [libplctag v2.6.0](https://github.com/libplctag/libplctag/tree/v2.6.0). Upstream offers MPL-2.0 or LGPL-2.0; this integration uses the MPL-2.0 option. Retain component notices and provide the corresponding upstream sources when redistributing the optional worker. The unchanged MPL license is preserved in [third_party/LIBPLCTAG-MPL-2.0.txt](third_party/LIBPLCTAG-MPL-2.0.txt) and copied with these notices into the optional publish output. No upstream adapter source was copied or modified; symbolic planning, policy integration and the read-only worker are new application code. This optional dependency does not change the root MIT license.
