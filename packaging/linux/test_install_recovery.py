"""Keep original metadata recoverable when restoring an installation runs out of disk space."""

import base64
import json
import stat
import subprocess
import sys
import unittest

import test_install


CHILD = r"""
import errno
import importlib.util
import os
import sys
from pathlib import Path

helper = sys.argv[1]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("flevert_installer_under_test", helper)
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
original_replace = installer.replace_file
manifest = Path(os.environ["XDG_DATA_HOME"]) / "Flevert" / ".flevert-install.json"
manifest_writes = 0

def disk_full_on_manifest_restore(path, saved):
    global manifest_writes
    if path == manifest:
        manifest_writes += 1
        if manifest_writes == 2:
            raise OSError(errno.ENOSPC, "injected disk full during manifest recovery", str(path))
    return original_replace(path, saved)

installer.replace_file = disk_full_on_manifest_restore
sys.argv = [helper]
installer.main()
"""


@unittest.skipUnless(sys.platform == "linux", "The packaged installer uses Linux installation paths")
class InstallRecoveryTests(unittest.TestCase):
    setUp = test_install.InstallTests.setUp
    tearDown = test_install.InstallTests.tearDown
    run_install = test_install.InstallTests.run_install

    def test_failed_metadata_restore_preserves_recovery_after_the_previous_app_is_restored(self):
        (self.package / "release.txt").write_text("old release")
        installed = self.run_install()
        self.assertEqual(0, installed.returncode, installed.stdout + installed.stderr)
        root = self.data / "Flevert"
        app = root / "app"
        before_app = {file.relative_to(app): file.read_bytes() for file in app.rglob("*") if file.is_file()}
        manifest = root / ".flevert-install.json"
        before_manifest = manifest.read_bytes()
        before_mode = stat.S_IMODE(manifest.stat().st_mode)
        (self.package / "release.txt").write_text("new release")
        (self.package / "new.txt").write_text("new release file")
        (self.package / "flevert").write_text(
            '#!/bin/sh\nif [ "$1" = "install-menu" ]; then exit 17; fi\n')
        environment = {"HOME": str(self.home), "PATH": "/usr/bin:/bin", "XDG_DATA_HOME": str(self.data)}

        failed = subprocess.run(
            [sys.executable, "-I", "-c", CHILD, str(self.package / "install.py")],
            cwd=self.dir, env=environment, capture_output=True, text=True, timeout=60)

        self.assertNotEqual(0, failed.returncode, failed.stdout + failed.stderr)
        self.assertIn("injected disk full during manifest recovery", failed.stderr)
        self.assertEqual(before_app,
                         {file.relative_to(app): file.read_bytes() for file in app.rglob("*") if file.is_file()})
        recovery = [directory for directory in root.glob(".flevert-install-*") if directory.is_dir()]
        self.assertEqual(1, len(recovery), "A failed metadata restore must retain its recovery directory")
        snapshots = json.loads((recovery[0] / ".recovery.json").read_text())
        self.assertEqual({"manifest", "menu", "extension"}, set(snapshots))
        self.assertIsInstance(snapshots["manifest"], dict)
        self.assertEqual(before_manifest, base64.b64decode(snapshots["manifest"]["content"], validate=True))
        self.assertIs(type(snapshots["manifest"]["mode"]), int)
        self.assertEqual(before_mode, snapshots["manifest"]["mode"])


if __name__ == "__main__":
    unittest.main()
