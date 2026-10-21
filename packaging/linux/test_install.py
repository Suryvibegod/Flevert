"""Tests for packaging/linux/install.sh, run from an unpacked package with a stand-in flevert that records what it is
asked, and HOME and XDG_DATA_HOME in a temporary folder.

Run: python3 -I -m unittest discover -s packaging/linux -p 'test_*.py'
"""

import os
import shutil
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

INSTALL = Path(__file__).resolve().parent / "install.sh"


class InstallTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)
        self.home = self.dir / "home"
        self.home.mkdir(mode=0o755)
        self.package = self.dir / "flevert-1.0.0"
        self.package.mkdir()
        shutil.copy(INSTALL, self.package / "install.sh")
        self.calls = self.dir / "calls"
        flevert = self.package / "flevert"
        flevert.write_text(f"#!/bin/sh\necho \"$(dirname \"$0\") $*\" >> '{self.calls}'\n")
        flevert.chmod(0o755)
        (self.package / "nautilus").mkdir()
        (self.package / "nautilus" / "flevert_menu.py").write_text("# extension")
        self.data = self.home / ".local" / "share"

    def tearDown(self):
        self._tmp.cleanup()

    def run_install(self, *arguments):
        environment = {"HOME": str(self.home), "PATH": "/usr/bin:/bin", "XDG_DATA_HOME": str(self.data)}
        return subprocess.run(["/bin/sh", str(self.package / "install.sh"), *arguments], env=environment,
                              capture_output=True, text=True, timeout=60)

    def test_install_copies_the_package_into_a_private_folder_and_puts_flevert_in_the_menu(self):
        run = self.run_install()

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        app = self.data / "Flevert" / "app"
        self.assertTrue((app / "flevert").exists())
        self.assertEqual("# extension", (app / "nautilus" / "flevert_menu.py").read_text())
        self.assertEqual(0o700, stat.S_IMODE((self.data / "Flevert").stat().st_mode))
        self.assertEqual(0, stat.S_IMODE(app.stat().st_mode) & 0o022)
        self.assertEqual([f"{app} install-menu"], self.calls.read_text().splitlines())

    def test_installing_again_replaces_the_app_and_leaves_no_other_copy(self):
        self.run_install()
        (self.package / "new.txt").write_text("new")

        run = self.run_install()

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertEqual(["app"], sorted(p.name for p in (self.data / "Flevert").iterdir()))
        self.assertTrue((self.data / "Flevert" / "app" / "new.txt").exists())

    def test_uninstall_takes_flevert_out_of_the_menu_and_removes_its_folder(self):
        self.run_install()
        (self.data / "Flevert" / "logs").mkdir()

        run = self.run_install("--uninstall")

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertEqual(f"{self.data / 'Flevert' / 'app'} uninstall-menu", self.calls.read_text().splitlines()[-1])
        self.assertFalse((self.data / "Flevert").exists())

    def test_an_unknown_option_changes_nothing(self):
        run = self.run_install("--everything")

        self.assertNotEqual(0, run.returncode)
        self.assertFalse((self.data / "Flevert").exists())


if __name__ == "__main__":
    unittest.main()
