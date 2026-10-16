# Project collaboration

- Commit completed, verified logical changes frequently. Keep unrelated work in separate commits.
- Do not push to origin. The user handles the actual push to `https://github.com/votecoffee/CodexStudio5000Plugin`.
- Domain operations belong in the shared service/Core. MCP and desktop code call that service rather than owning separate project mutations.
- Default validation is local and uses preserved copies or generated fixtures. Development requests do not authorize controller connections. Live operations require an explicitly identified and authorized target/action; honor any program/routine and tag-scope limits. Let the user operate Studio when they request hands-off UI control.
- Keep installed-SDK bindings, native Studio observations and functional/controller validation distinct. SDK controller Test mode is not Test Edits. SDK 2.1.974 has no standalone Test/Untest/Assemble API; use the documented native workflow instead of inventing methods.
