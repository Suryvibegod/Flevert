"""Tests for scripts/write-notices-linux.py, which writes THIRD-PARTY-NOTICES.txt into the Linux publish folder and fails
rather than let the package ship a file without its licence, as write-notices.ps1 does for the Windows installer.

Run: python3 -I -m unittest discover -s scripts -p 'test_*.py'
"""

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent / "write-notices-linux.py"


class WriteNoticesLinuxTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)
        self.publish, self.packages, self.root = self.dir / "publish", self.dir / "packages", self.dir / "root"
        for folder in (self.publish, self.packages, self.root):
            folder.mkdir()
        # A package with its licence, its assembly shipped; flevert's own files.
        (self.packages / "some.package" / "1.0.0").mkdir(parents=True)
        (self.packages / "some.package" / "1.0.0" / "LICENSE.TXT").write_text("Some licence")
        for name in ["flevert", "flevert.dll", "flevert.deps.json", "flevert.runtimeconfig.json", "Some.Package.dll", "openssl.cnf"]:
            (self.publish / name).write_text("x")
        (self.publish / "nautilus").mkdir()
        (self.publish / "nautilus" / "flevert_menu.py").write_text("x")
        self.deps({"Some.Package/1.0.0": {"type": "package"}, "Flevert.Core/1.0.0": {"type": "project"}},
                  {"Some.Package/1.0.0": {"runtime": {"lib/net10.0/Some.Package.dll": {}}},
                   "flevert/1.0.0": {"runtime": {"flevert.dll": {}}}})
        (self.root / "deps").mkdir()
        (self.root / "deps" / "tool-LICENSE").write_text("Tool licence")
        self.manifest([{"title": "A tool 1.0", "files": ["libtool.so"], "licences": ["deps/tool-LICENSE"]}])
        (self.publish / "libtool.so").write_text("x")

    def tearDown(self):
        self._tmp.cleanup()

    def deps(self, libraries, target):
        (self.publish / "flevert.deps.json").write_text(json.dumps(
            {"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/linux-x64"}, "libraries": libraries,
             "targets": {".NETCoreApp,Version=v10.0/linux-x64": target}}))

    def manifest(self, notices):
        self.manifest_path = self.dir / "deps-linux.json"
        self.manifest_path.write_text(json.dumps({"notices": notices}))

    def run_script(self):
        return subprocess.run([sys.executable, "-I", str(SCRIPT), "--publish", str(self.publish), "--packages", str(self.packages),
                               "--manifest", str(self.manifest_path), "--root", str(self.root)], capture_output=True, text=True)

    def test_every_package_and_listed_file_gets_its_licence_in_the_notices(self):
        run = self.run_script()

        self.assertEqual(0, run.returncode, run.stderr)
        notices = (self.publish / "THIRD-PARTY-NOTICES.txt").read_text()
        self.assertIn("Some.Package 1.0.0 - LICENSE.TXT", notices)
        self.assertIn("Some licence", notices)
        self.assertIn("A tool 1.0 - tool-LICENSE", notices)
        self.assertIn("Shipped as: libtool.so", notices)
        self.assertIn("Tool licence", notices)

    def test_a_package_without_a_licence_file_fails(self):
        (self.packages / "some.package" / "1.0.0" / "LICENSE.TXT").unlink()

        run = self.run_script()

        self.assertNotEqual(0, run.returncode)
        self.assertIn("Some.Package", run.stderr)
        self.assertFalse((self.publish / "THIRD-PARTY-NOTICES.txt").exists())

    def test_a_shipped_file_nothing_covers_fails_and_is_named(self):
        (self.publish / "libstray.so").write_text("x")

        run = self.run_script()

        self.assertNotEqual(0, run.returncode)
        self.assertIn("libstray.so", run.stderr)

    def test_a_listed_file_the_build_does_not_ship_fails(self):
        (self.publish / "libtool.so").unlink()

        run = self.run_script()

        self.assertNotEqual(0, run.returncode)
        self.assertIn("libtool.so", run.stderr)

    def test_a_licence_that_is_missing_fails(self):
        (self.root / "deps" / "tool-LICENSE").unlink()

        run = self.run_script()

        self.assertNotEqual(0, run.returncode)
        self.assertIn("deps/tool-LICENSE", run.stderr)

    def test_the_real_manifest_names_a_licence_for_every_file_it_lists(self):
        manifest = json.loads((SCRIPT.parent / "deps-linux.json").read_text())

        for entry in manifest["notices"]:
            self.assertTrue(entry["files"], entry["title"])
            self.assertTrue(entry["licences"], entry["title"])


if __name__ == "__main__":
    unittest.main()
