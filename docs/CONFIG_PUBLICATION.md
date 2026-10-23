# Codex configuration publication

Treat a user's Codex TOML as user-owned state. Documentation, source checks and installer fixtures must not rewrite it. Use a fresh disposable profile and explicit authorization for any eventual real binding change; preserve exact original bytes and uncertain results. Do not rerun historical private installation/publication scripts as tests.

## Byte contract

The app-owned TOML writer is `scripts/codex_connection.py`. It reads bytes, decodes UTF-8 with optional BOM, checks TOML semantics and changes only the reviewed Rockwell connection fields. Existing explicit LF or CRLF, Unicode, multiline values, comments and BOM are preserved. New connection lines follow the existing style. Unrelated parsed settings must remain equal.

Publication uses a binary temporary file, flush/fsync, a compare-before-replace source check, atomic replacement and exact byte readback. Existing configuration is backed up with `write_bytes`. There is no Windows text newline translation in this path. Both source and final bytes are checked before backup/publication; lone CR and CRCRLF are refused. The app does not silently normalize malformed user text or repair a live configuration.

Windows text-mode output can translate an LF into CRLF. Feeding already explicit CRLF through that translation produces CRCRLF. A focused fixture reproduces this with an explicit `TextIOWrapper(newline="\r\n")`; it demonstrates the mechanism, not the cause of any particular user file. Do not attribute a reported malformed configuration to an app, writer or historical run without its actual input/output bytes and execution evidence.

## Publication paths

| Path | Contract |
| --- | --- |
| `scripts/codex_connection.py` | App-owned TOML byte writer; explicit reviewed apply only, malformed-EOL refusal and semantic preservation |
| `scripts/CodexIntegration.ps1` / `Configure-Codex.ps1` | App-owned JSON package/receipt writers plus explicitly delegated Codex CLI plugin operations; they do not format TOML themselves |
| `scripts/Verify-CodexPluginCli.ps1` | Real-CLI test utility with its own disposable profile; .NET `WriteAllText` is used for that fixture's TOML, without Windows text-mode translation |
| Private publication helpers | Audit direct and delegated writers, their file modes, explicit newline conversion and exact bytes before reusing; preserve executed evidence rather than editing old runs |

CLI implementation is an independent writer. A package install/readiness result is not evidence of what caused a newline problem. Do not invoke it against a live profile merely to investigate formatting.

## Fixture coverage and evidence

```powershell
python -m unittest discover -s tests/python -p "test_codex*.py"
```

Fixtures cover CRLF and LF input, with/without BOM, UTF-8 Unicode, multiline basic/literal values, parsed settings/unrelated server preservation, exact backup/readback and idempotent binding plans. Malformed source/output is rejected before writes, and the Windows double-translation mechanism is tested separately. No test reads or writes the user's live Codex profile or invokes a real Codex install.

For an investigation retain generated before/after bytes, SHA-256, newline counts and hex excerpts under ignored `artifacts/` or `.local/private-docs/`. Record the exact writer, test environment, scope and whether the cause was observed or remains unproven. Repository docs contain reusable contracts; personal config, reports and deployment receipts remain local.
