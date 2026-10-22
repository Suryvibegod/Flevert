"""Process-level regressions for installer locking and interrupted installers."""

import subprocess
import sys
import time
import unittest

import test_install


CHILD = r"""
import importlib.util
import sys
import time
from pathlib import Path

helper, hook, ready, release = sys.argv[1:]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("flevert_installer_under_test", helper)
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)
original = getattr(installer, hook)

def pause(*args, **kwargs):
    # Ownership validation must already be protected by the lock when this returns.
    result = original(*args, **kwargs) if hook == "installed_entries" else None
    Path(ready).write_text("ready")
    deadline = time.monotonic() + 60
    while not Path(release).exists():
        if time.monotonic() >= deadline:
            raise TimeoutError("The parent did not release the installer barrier")
        time.sleep(0.02)
    return result if hook == "installed_entries" else original(*args, **kwargs)

setattr(installer, hook, pause)
sys.argv = [helper]
installer.main()
"""


@unittest.skipUnless(sys.platform == "linux", "The packaged installer uses Linux file locking")
class InstallLockTests(unittest.TestCase):
    run_install = test_install.InstallTests.run_install

    def setUp(self):
        test_install.InstallTests.setUp(self)
        self.children = []

    def tearDown(self):
        try:
            for child in self.children:
                self.stop_child(child)
        finally:
            test_install.InstallTests.tearDown(self)

    def start_paused_installer(self, hook):
        ready = self.dir / "child-ready"
        release = self.dir / "child-release"
        environment = {"HOME": str(self.home), "PATH": "/usr/bin:/bin", "XDG_DATA_HOME": str(self.data)}
        child = subprocess.Popen(
            [sys.executable, "-I", "-c", CHILD, str(self.package / "install.py"), hook,
             str(ready), str(release)],
            cwd=self.dir, env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.children.append(child)
        deadline = time.monotonic() + 30
        while not ready.exists():
            if child.poll() is not None:
                stdout, stderr = child.communicate(timeout=5)
                self.fail(f"Installer exited before reaching {hook}: {stdout}{stderr}")
            if time.monotonic() >= deadline:
                self.fail(f"Installer never reached the {hook} barrier")
            time.sleep(0.02)
        self.assertIsNone(child.poll(), "The barrier must keep the installer running")
        return child, release

    @staticmethod
    def stop_child(child):
        if child.poll() is None:
            child.kill()
        child.communicate(timeout=10)

    def test_ownership_validation_is_already_protected_by_the_install_lock(self):
        (self.package / "release.txt").write_text("old release")
        installed = self.run_install()
        self.assertEqual(0, installed.returncode, installed.stdout + installed.stderr)
        root = self.data / "Flevert"
        app = root / "app"
        before_files = {file.relative_to(app): file.read_bytes() for file in app.rglob("*") if file.is_file()}
        before_manifest = (root / ".flevert-install.json").read_bytes()
        before_calls = self.calls.read_text()
        (self.package / "release.txt").write_text("new release")
        (self.package / "new.txt").write_text("new release file")
        child, release = self.start_paused_installer("installed_entries")

        competing = self.run_install()

        self.assertNotEqual(0, competing.returncode, "A second installer must refuse while ownership is being validated")
        self.assertEqual(before_files,
                         {file.relative_to(app): file.read_bytes() for file in app.rglob("*") if file.is_file()})
        self.assertEqual(before_manifest, (root / ".flevert-install.json").read_bytes())
        self.assertEqual(before_calls, self.calls.read_text())
        release.write_text("continue")
        stdout, stderr = child.communicate(timeout=60)
        self.assertEqual(0, child.returncode, stdout + stderr)
        self.assertEqual("new release", (app / "release.txt").read_text())
        self.assertEqual("new release file", (app / "new.txt").read_text())

    def test_killing_the_lock_holder_does_not_block_the_next_install(self):
        installed = self.run_install()
        self.assertEqual(0, installed.returncode, installed.stdout + installed.stderr)
        (self.package / "new.txt").write_text("next release")
        child, _ = self.start_paused_installer("install")

        child.kill()
        child.communicate(timeout=10)
        self.assertNotEqual(0, child.returncode)
        retried = self.run_install()

        self.assertEqual(0, retried.returncode, retried.stdout + retried.stderr)
        root = self.data / "Flevert"
        self.assertEqual("next release", (root / "app" / "new.txt").read_text())
        self.assertFalse((root / ".flevert-install.lock").exists())


if __name__ == "__main__":
    unittest.main()
