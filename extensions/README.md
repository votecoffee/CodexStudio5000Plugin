Installed extension packages are generated here by `scripts/Build-Extensions.ps1` or `scripts/Publish-Extension.ps1` and ignored by Git. Each module has `active.json` and immutable `versions/<version>/manifest.json` plus published DLLs/dependencies. Do not edit a loaded version; publish a new version and use `studio_extensions_reload`, the GUI or `scripts/Reload-Extension.ps1`.

See [the extension guide](../docs/EXTENSIONS.md) for the one-time bridge installation, development, trust boundary, live catalog and reload/rollback workflow.
