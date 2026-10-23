"""Generated configuration only: no Codex process, Windows service or PLC access."""
import importlib.util
import json
from pathlib import Path
import tempfile
import tomllib
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("codex_connection", Path(__file__).resolve().parents[2] / "scripts/codex_connection.py")
C = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(C)


class ConnectionTests(unittest.TestCase):
    def test_inventory_uses_the_explicit_selected_profile_without_launching_mcp(self):
        with patch.object(C.shutil, "which", return_value="codex.exe"), patch.object(C.subprocess, "run") as run:
            run.return_value.stdout = json.dumps({"installed": [{"pluginId": C.PLUGIN, "installed": True}]}).encode()
            C.inventory(Path("/selected-profile"))
            self.assertEqual(str(Path("/selected-profile")), run.call_args.kwargs["env"]["CODEX_HOME"])
            self.assertEqual(["codex.exe", "plugin", "list", "--json"], run.call_args.args[0])

    def original(self, extra="", newline="\n"):
        return (f'''# keep comment
model = 'example'
[plugins."{C.PLUGIN}"]
enabled = true
[plugins."{C.PLUGIN}".mcp_servers.rockwell-mcp-server]
enabled = true # preserve comment
{extra}
[mcp_servers.other]
command = 'other.exe'
[mcp_servers.other.env]
UNCHANGED = 'private fixture'
'''.replace("\n", newline)).encode()

    def plan(self, data):
        return C.plan_binding(data, Path("/cache/release"), Path("/windows"))

    def test_exactly_one_route_and_unrelated_values_comments_survive(self):
        original = self.original()
        updated, plan = self.plan(original)
        before, after = tomllib.loads(original.decode()), tomllib.loads(updated.decode())
        self.assertEqual(before["mcp_servers"]["other"], after["mcp_servers"]["other"])
        self.assertEqual(before["model"], after["model"])
        self.assertIn(b"# keep comment", updated)
        self.assertIn(b"enabled = false # preserve comment", updated)
        self.assertTrue(after["plugins"][C.PLUGIN]["enabled"])
        self.assertFalse(after["plugins"][C.PLUGIN]["mcp_servers"][C.SERVER]["enabled"])
        self.assertEqual(1, plan["enabledRoutes"])
        self.assertNotIn("${PLUGIN_ROOT}", json.dumps(after["mcp_servers"][C.SERVER]))
        self.assertEqual("not_observed", plan["chatActivation"])

    def test_repeated_plan_is_identical_and_crlf_bom_survive(self):
        updated, _ = self.plan(b"\xef\xbb\xbf" + self.original(newline="\r\n"))
        repeated, _ = self.plan(updated)
        self.assertEqual(updated, repeated)
        self.assertTrue(updated.startswith(b"\xef\xbb\xbf"))
        self.assertNotIn(b"\n", updated.replace(b"\r\n", b""))

    def test_missing_server_table_uses_explicit_enable_override(self):
        original = f'[plugins."{C.PLUGIN}"]\nenabled = true\n'.encode()
        updated, _ = self.plan(original)
        self.assertFalse(tomllib.loads(updated.decode())["plugins"][C.PLUGIN]["mcp_servers"][C.SERVER]["enabled"])

    def test_quoted_alternative_table_syntax(self):
        original = self.original().replace(f'"{C.PLUGIN}".mcp_servers.rockwell-mcp-server'.encode(),
                                           f"'{C.PLUGIN}'.mcp_servers.'rockwell-mcp-server'".encode())
        updated, _ = self.plan(original)
        self.assertFalse(tomllib.loads(updated.decode())["plugins"][C.PLUGIN]["mcp_servers"][C.SERVER]["enabled"])

    def test_disabled_plugin_is_not_silently_enabled(self):
        with self.assertRaisesRegex(ValueError, "Enable the installed"):
            self.plan(self.original().replace(b"enabled = true", b"enabled = false", 1))

    def test_restrictive_bundled_tool_policy_cannot_be_bypassed(self):
        for extra in ('enabled_tools = ["read"]', 'default_tools_approval_mode = "prompt"',
                      '[plugins."' + C.PLUGIN + '".mcp_servers.rockwell-mcp-server.tools.write]\napproval_mode = "prompt"'):
            with self.subTest(extra=extra), self.assertRaisesRegex(ValueError, "policy requires explicit migration"):
                self.plan(self.original(extra))

    def test_conflicting_direct_route_is_preserved(self):
        with self.assertRaisesRegex(ValueError, "existing direct Rockwell route differs"):
            self.plan(self.original() + b'\n[mcp_servers.rockwell-mcp-server]\ncommand = "unexpected.exe"\n')

    def test_table_looking_multiline_string_cannot_change_other_semantics(self):
        original = f'''text = ''' + "'''\n[plugins.\"" + C.PLUGIN + "\".mcp_servers.rockwell-mcp-server]\nenabled = true\n'''\n"
        with self.assertRaises(ValueError):
            self.plan(original.encode() + self.original())

    def test_apply_backs_up_exact_bytes_and_reads_back_without_install_or_restart(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / "config.toml"
            original = self.original()
            path.write_bytes(original)
            updated, _ = self.plan(original)
            result = C.apply_binding(path, original, updated, root / "backup", C.sha(original))
            self.assertEqual(original, (root / "backup/config.toml.before").read_bytes())
            self.assertEqual(updated, path.read_bytes())
            self.assertFalse(result["serviceRestarted"])
            self.assertFalse(result["reinstalled"])

    def test_review_or_concurrent_configuration_change_refuses_write(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / "config.toml"
            original = self.original()
            updated, _ = self.plan(original)
            path.write_bytes(original)
            with self.assertRaisesRegex(ValueError, "SHA-256"):
                C.apply_binding(path, original, updated, root / "wrong-hash", "0" * 64)
            self.assertFalse((root / "wrong-hash").exists())
            path.write_bytes(original + b"# concurrent edit\n")
            with self.assertRaisesRegex(ValueError, "changed after review"):
                C.apply_binding(path, original, updated, root / "concurrent", C.sha(original))
            self.assertEqual(original + b"# concurrent edit\n", path.read_bytes())
            self.assertEqual(original, (root / "concurrent/config.toml.before").read_bytes())

    def test_chat_visibility_requires_separate_caller_inventory(self):
        self.assertEqual("unknown", C.chat_visibility(None)["state"])
        self.assertEqual("reported_missing", C.chat_visibility({"toolNames": ["other_tool"]})["state"])
        result = C.chat_visibility({"toolNames": ["mcp__rockwell__rockwell_broker_capabilities"]})
        self.assertEqual("reported_present", result["state"])
        self.assertEqual("not_observed_by_this_script", result["successfulChatCall"])
        with self.assertRaises(ValueError):
            C.chat_visibility({"toolNames": "not a list"})


    def test_admission_reports_retained_claims_without_inferring_liveness_or_cleanup(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / ".local").mkdir()
            resources = root / "registry/resources"
            resources.mkdir(parents=True)
            (root / ".local/service-owner.json").write_text(json.dumps({"registry": str(resources.parent)}))
            for i, state in enumerate(("active", "launching", "terminated", "released", "failed")):
                (resources / f"{i}.json").write_text(json.dumps({"app_id": C.SERVER, "state": state, "process": None}))
            result = C.resource_admission(root, 2)
            self.assertEqual("at_limit", result["state"])
            self.assertEqual(2, result["retainedNonterminalClaims"])
            self.assertFalse(result["isLiveProcessCount"])
            self.assertFalse(result["cleanupAttempted"])
            self.assertEqual("capacity_available", C.resource_admission(root, 3)["state"])


if __name__ == "__main__":
    unittest.main()
