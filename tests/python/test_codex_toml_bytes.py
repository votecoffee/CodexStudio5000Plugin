"""Disposable byte fixtures only. Never reads/writes the user's Codex profile."""
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("codex_connection_bytes", Path(__file__).resolve().parents[2] / "scripts/codex_connection.py")
C = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(C)


def fixture(newline, bom=False):
    text = '''# generated Unicode fixture Ω
model = "fixture-only"
note = """First line Δ
Second line: preserve this value
"""
literal = ''' + "'''C:\\Symbols\\München\\part name'''" + f'''
[plugins."{C.PLUGIN}"]
enabled = true
[plugins."{C.PLUGIN}".mcp_servers.rockwell-mcp-server]
enabled = true
[mcp_servers.other]
command = "unrelated.exe"
args = ["a b", "日本語"]
'''
    return (b"\xef\xbb\xbf" if bom else b"") + text.replace("\n", newline).encode("utf-8")


class TomlBytesTests(unittest.TestCase):
    def test_binary_publication_preserves_crlf_or_lf_unicode_multiline_and_unrelated_settings(self):
        for newline in ("\r\n", "\n"):
            for bom in (False, True):
                with self.subTest(newline=repr(newline), bom=bom), tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    path = root / "disposable config.toml"
                    original = fixture(newline, bom)
                    path.write_bytes(original)
                    before = C.parse_toml_bytes(original)
                    updated, _ = C.plan_binding(original, root / "cached plugin Ω", root / "Windows")
                    C.apply_binding(path, original, updated, root / "backup", C.sha(original))
                    actual = path.read_bytes()
                    after = C.parse_toml_bytes(actual)
                    self.assertEqual(updated, actual)
                    self.assertEqual(original, (root / "backup/config.toml.before").read_bytes())
                    self.assertEqual(bom, actual.startswith(b"\xef\xbb\xbf"))
                    self.assertNotIn(b"\r\r\n", actual)
                    self.assertNotIn(b"\r", actual.replace(b"\r\n", b""))
                    if newline == "\r\n":
                        self.assertNotIn(b"\n", actual.replace(b"\r\n", b""))
                    else:
                        self.assertNotIn(b"\r", actual)
                    for key in ("model", "note", "literal"):
                        self.assertEqual(before[key], after[key])
                    self.assertEqual(before["mcp_servers"]["other"], after["mcp_servers"]["other"])
                    self.assertIn("日本語".encode(), actual)

    def test_windows_text_translation_mechanism_is_reproducible_but_not_used_by_the_writer(self):
        stream = io.BytesIO()
        text = io.TextIOWrapper(stream, encoding="utf-8", newline="\r\n")
        text.write('fixture = "Ω"\r\n')
        text.flush()
        actual = stream.getvalue()
        self.assertEqual('fixture = "Ω"\r\r\n'.encode(), actual)
        with self.assertRaisesRegex(ValueError, "Malformed TOML newline"):
            C.parse_toml_bytes(actual)

    def test_malformed_source_or_output_is_refused_before_backup_or_publication(self):
        for suffix in (b"\r", b"\r\r\n"):
            with self.subTest(suffix=suffix), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                path = root / "config.toml"
                original = fixture("\r\n")
                path.write_bytes(original)
                with self.assertRaisesRegex(ValueError, "Malformed TOML newline"):
                    C.plan_binding(original + suffix, root / "cache", root / "Windows")
                updated, _ = C.plan_binding(original, root / "cache", root / "Windows")
                with self.assertRaisesRegex(ValueError, "Malformed TOML newline"):
                    C.apply_binding(path, original, updated + suffix, root / "backup", C.sha(original))
                self.assertFalse((root / "backup").exists())
                self.assertEqual(original, path.read_bytes())


if __name__ == "__main__":
    unittest.main()
