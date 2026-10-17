# Third-party provenance

`third_party/L5xploderLib` was copied from `src/L5xploderLib` in the `ra-logix-designer-vcs-custom-tools-main` source distribution.

The source distribution's license is MIT, copyright (c) 2025 Rockwell Automation. Its full text is preserved in [third_party/ROCKWELL-LICENSE.txt](third_party/ROCKWELL-LICENSE.txt). Archive and source hashes are recorded in [docs/upstream-manifest.json](docs/upstream-manifest.json). The manifest identifies the distribution without recording a contributor's local filesystem paths. Any changes to the copied source must be recorded in that manifest.

The optional `Studio5000.NativeBridge` uses RockwellAutomation.LogixDesigner.CSClient 2.1.974 from the user's installed local SDK feed. Its proprietary binaries and FactoryTalk helper are only local build artifacts and are excluded from Git. This notice does not grant redistribution rights to SDK binaries. The bridge explicitly includes the SDK's FtspAdapter.exe in its local publish output because the package's build-only copy target does not include it in `dotnet publish`.

The project also restores YamlDotNet, the official ModelContextProtocol C# SDK, Microsoft .NET libraries, and test dependencies from NuGet. Exact dependency versions are pinned in package lock files; see each package's license. No proprietary archive sample PLC projects are committed as examples.
