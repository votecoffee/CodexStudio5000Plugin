"""Exercise actual CMD wrappers in paths with spaces using file-only stubs."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[2]


@unittest.skipUnless(os.name == "nt", "Windows CMD/PowerShell wrapper contract")
class RootCmdTests(unittest.TestCase):
    def invoke(self, root, name, arguments, env):
        # All command fragments are fixed test values/new temporary paths;
        # neither customer data nor an arbitrary shell command is accepted.
        quoted = '"' + str(root / name) + '" ' + subprocess.list2cmdline(arguments)
        command = f'"{os.environ["COMSPEC"]}" /d /s /c "{quoted}"'
        return subprocess.run(command, cwd=root.parent, env=env, capture_output=True,
                              timeout=20, creationflags=0x08000000)

    def setup_root(self, directory, wrapper):
        root = Path(directory) / "generated application Ω with spaces"
        (root / "scripts").mkdir(parents=True)
        shutil.copyfile(REPO / wrapper, root / wrapper)
        env = dict(os.environ)
        env["CODEX_HOME"] = str(root / "disposable Codex home")
        env["PSModulePath"] = "ROCKWELL_INVALID_INHERITED_MODULE_MARKER"
        env["ROCKWELL_CMD_PROOF"] = str(root / "proof.json")
        return root, env

    def test_argument_forwarding_unicode_spaces_and_module_environment(self):
        for wrapper, script, arguments in (
            ("Install.cmd", "Install.ps1", ["-SkipBuild", "-NoShortcut", "-CodexIntegration", "None"]),
            ("Run.cmd", "Start.ps1", ["-ProjectPath", "C:\\Generated Ω with spaces\\fixture.L5X"]),
        ):
            with self.subTest(wrapper=wrapper), tempfile.TemporaryDirectory() as directory:
                root, env = self.setup_root(directory, wrapper)
                (root / "scripts" / script).write_bytes(b"[IO.File]::WriteAllText($env:ROCKWELL_CMD_PROOF, (@{ arguments=@($args); modules=$env:PSModulePath } | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))\r\nexit 0\r\n")
                result = self.invoke(root, wrapper, arguments, env)
                self.assertEqual(0, result.returncode, result.stderr.decode(errors="replace"))
                proof = json.loads((root / "proof.json").read_bytes())
                self.assertEqual(arguments, proof["arguments"])
                self.assertNotIn("ROCKWELL_INVALID_INHERITED_MODULE_MARKER", proof["modules"])

    def test_failure_exit_code_is_propagated(self):
        for wrapper, script in (("Install.cmd", "Install.ps1"), ("Run.cmd", "Start.ps1")):
            with self.subTest(wrapper=wrapper), tempfile.TemporaryDirectory() as directory:
                root, env = self.setup_root(directory, wrapper)
                (root / "scripts" / script).write_bytes(b"exit 37\r\n")
                self.assertEqual(37, self.invoke(root, wrapper, [], env).returncode)

    def test_actual_install_and_run_help_does_not_enter_application_work(self):
        for wrapper, script in (("Install.cmd", "Install.ps1"), ("Run.cmd", "Start.ps1")):
            with self.subTest(wrapper=wrapper), tempfile.TemporaryDirectory() as directory:
                root, env = self.setup_root(directory, wrapper)
                shutil.copyfile(REPO / "scripts" / script, root / "scripts" / script)
                for flag in ("-?", "-Help", "--help", "/?"):
                    result = self.invoke(root, wrapper, [flag], env)
                    self.assertEqual(0, result.returncode, result.stderr.decode(errors="replace"))
                    self.assertIn(wrapper.encode(), result.stdout)
                self.assertFalse((root / ".local").exists())
                self.assertFalse((root / "service.local.json").exists())
                self.assertFalse(Path(env["CODEX_HOME"]).exists())


if __name__ == "__main__":
    unittest.main()
