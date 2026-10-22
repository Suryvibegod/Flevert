"""Tests for packaging/linux/install.sh, run from an unpacked package with a stand-in flevert that records what it is
asked, and HOME and XDG_DATA_HOME in a temporary folder.

Run: python3 -I -m unittest discover -s packaging/linux -p 'test_*.py'
"""

import json
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
        helper = INSTALL.with_name("install.py")
        if helper.exists():
            shutil.copy(helper, self.package / "install.py")
        self.calls = self.dir / "calls"
        flevert = self.package / "flevert"
        flevert.write_text(f"#!/bin/sh\necho \"$(dirname \"$0\") $*\" >> '{self.calls}'\n")
        flevert.chmod(0o755)
        (self.package / "nautilus").mkdir()
        (self.package / "nautilus" / "flevert_menu.py").write_text("# extension")
        self.data = self.home / ".local" / "share"

    def tearDown(self):
        self._tmp.cleanup()

    def run_install(self, *arguments, data_home=None, umask=-1):
        environment = {"HOME": str(self.home), "PATH": "/usr/bin:/bin",
                       "XDG_DATA_HOME": str(self.data if data_home is None else data_home)}
        return subprocess.run(["/bin/sh", str(self.package / "install.sh"), *arguments], env=environment,
                              cwd=self.dir, capture_output=True, text=True, timeout=60, umask=umask)

    def test_uninstall_refuses_linked_extension_ancestors_before_running_or_deleting_anything(self):
        for linked_parent in ("nautilus-python", "extensions"):
            with self.subTest(parent=linked_parent):
                data = self.dir / linked_parent
                installed = self.run_install(data_home=data)
                self.assertEqual(0, installed.returncode, installed.stdout + installed.stderr)
                app = data / "Flevert" / "app"
                executable = app / "flevert"
                with executable.open("a") as stream:
                    stream.write('if [ "$1" = "uninstall-menu" ]; then\n'
                                 'rm -f -- "$XDG_DATA_HOME/nautilus-python/extensions/flevert_menu.py"\nfi\n')
                target = self.dir / (linked_parent + "-unrelated")
                extensions = target / "extensions" if linked_parent == "nautilus-python" else target
                extensions.mkdir(parents=True)
                sentinel = extensions / "flevert_menu.py"
                sentinel.write_text("unrelated extension")
                parent = data / "nautilus-python"
                if linked_parent == "extensions":
                    parent.mkdir(exist_ok=True)
                    parent = parent / "extensions"
                if parent.exists():
                    shutil.rmtree(parent)
                parent.symlink_to(target, target_is_directory=True)
                before_calls = self.calls.read_text()

                run = self.run_install("--uninstall", data_home=data)

                self.assertNotEqual(0, run.returncode, run.stdout + run.stderr)
                self.assertEqual("unrelated extension", sentinel.read_text())
                self.assertTrue(executable.exists())
                self.assertEqual(before_calls, self.calls.read_text())

    def test_new_extension_directories_are_private_even_with_a_group_writable_umask(self):
        executable = self.package / "flevert"
        with executable.open("a") as stream:
            stream.write('mkdir -p -- "$XDG_DATA_HOME/nautilus-python/extensions"\n')

        run = self.run_install(umask=0o002)

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        for folder in (self.data / "nautilus-python", self.data / "nautilus-python" / "extensions"):
            self.assertEqual(0o700, stat.S_IMODE(folder.stat().st_mode))

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
        expected = [".flevert-install.json", "app"] if (self.package / "install.py").exists() else ["app"]
        self.assertEqual(expected, sorted(p.name for p in (self.data / "Flevert").iterdir()))
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

    def test_a_symlinked_install_root_cannot_change_or_delete_an_unrelated_app(self):
        for name, arguments in (("install", ()), ("uninstall", ("--uninstall",))):
            with self.subTest(action=name):
                target = self.dir / f"unrelated-{name}"
                (target / "app").mkdir(parents=True)
                sentinel = target / "app" / "personal.txt"
                sentinel.write_text("unrelated app data")
                target.chmod(0o750)
                before_mode = stat.S_IMODE(target.stat().st_mode)
                data = self.dir / f"data-{name}"
                data.mkdir()
                (data / "Flevert").symlink_to(target, target_is_directory=True)

                run = self.run_install(*arguments, data_home=data)

                self.assertNotEqual(0, run.returncode, run.stdout + run.stderr)
                self.assertEqual("unrelated app data", sentinel.read_text())
                self.assertEqual(before_mode, stat.S_IMODE(target.stat().st_mode))
                self.assertTrue((data / "Flevert").is_symlink())
                self.assertFalse(self.calls.exists())

    def test_a_symlinked_data_ancestor_is_refused_before_any_install_or_uninstall(self):
        for name, arguments in (("install", ()), ("uninstall", ("--uninstall",))):
            with self.subTest(action=name):
                target = self.dir / f"real-data-{name}"
                root = target / "Flevert"
                (root / "app").mkdir(parents=True)
                sentinel = root / "app" / "personal.txt"
                sentinel.write_text("keep me")
                root.chmod(0o750)
                before_mode = stat.S_IMODE(root.stat().st_mode)
                linked_data = self.dir / f"linked-data-{name}"
                linked_data.symlink_to(target, target_is_directory=True)

                run = self.run_install(*arguments, data_home=linked_data)

                self.assertNotEqual(0, run.returncode, run.stdout + run.stderr)
                self.assertEqual("keep me", sentinel.read_text())
                self.assertEqual(before_mode, stat.S_IMODE(root.stat().st_mode))
                self.assertFalse(self.calls.exists())

    def test_an_app_without_installer_ownership_is_not_run_replaced_or_deleted(self):
        for name, arguments in (("install", ()), ("uninstall", ("--uninstall",))):
            with self.subTest(action=name):
                data = self.dir / f"unmanaged-data-{name}"
                app = data / "Flevert" / "app"
                app.mkdir(parents=True)
                sentinel = app / "personal.txt"
                sentinel.write_text("unmanaged data")
                shutil.copy(self.package / "flevert", app / "flevert")

                run = self.run_install(*arguments, data_home=data)

                self.assertNotEqual(0, run.returncode, run.stdout + run.stderr)
                self.assertEqual("unmanaged data", sentinel.read_text())
                self.assertTrue((app / "flevert").exists())
                self.assertFalse(self.calls.exists(), "An unmanaged executable must not be trusted for uninstall-menu.")

    def test_uninstall_keeps_an_unknown_file_beside_the_installed_app(self):
        installed = self.run_install()
        self.assertEqual(0, installed.returncode, installed.stdout + installed.stderr)
        root = self.data / "Flevert"
        note = root / "personal-note.txt"
        note.write_text("my note")
        for folder in ("logs", "magick", "pipes", "update", "media"):
            (root / folder).mkdir()
            (root / folder / "runtime-data.txt").write_text("owned runtime data")

        run = self.run_install("--uninstall")

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertEqual("my note", note.read_text())
        self.assertFalse((root / "app").exists())
        for folder in ("logs", "magick", "pipes", "update", "media"):
            self.assertFalse((root / folder).exists())

    def test_uninstall_removes_owned_app_entries_but_keeps_an_unknown_app_file(self):
        installed = self.run_install()
        self.assertEqual(0, installed.returncode, installed.stdout + installed.stderr)
        app = self.data / "Flevert" / "app"
        note = app / "personal-note.txt"
        note.write_text("my app note")

        run = self.run_install("--uninstall")

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertEqual("my app note", note.read_text())
        self.assertFalse((app / "flevert").exists())
        self.assertFalse((app / "nautilus" / "flevert_menu.py").exists())

    def test_update_refuses_unknown_app_files_and_preserves_the_previous_install(self):
        installed = self.run_install()
        self.assertEqual(0, installed.returncode, installed.stdout + installed.stderr)
        root = self.data / "Flevert"
        app = root / "app"
        note = app / "personal-note.txt"
        note.write_text("my app note")
        before_executable = (app / "flevert").read_bytes()
        manifest = root / ".flevert-install.json"
        before_manifest = manifest.read_bytes() if manifest.exists() else None
        before_calls = self.calls.read_text()
        (self.package / "new.txt").write_text("new release")

        run = self.run_install()

        self.assertNotEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertEqual("my app note", note.read_text())
        self.assertEqual(before_executable, (app / "flevert").read_bytes())
        if before_manifest is not None:
            self.assertEqual(before_manifest, manifest.read_bytes())
        self.assertFalse((app / "new.txt").exists())
        self.assertEqual(before_calls, self.calls.read_text())

    def test_a_relative_xdg_data_home_is_refused_without_creating_an_install(self):
        run = self.run_install(data_home="relative-data")

        self.assertNotEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertFalse((self.dir / "relative-data").exists())
        self.assertFalse(self.calls.exists())

    def test_manifest_path_traversal_cannot_authorize_deleting_a_file_outside_the_app(self):
        for name, arguments in (("install", ()), ("uninstall", ("--uninstall",))):
            with self.subTest(action=name):
                data = self.dir / f"tampered-data-{name}"
                installed = self.run_install(data_home=data)
                self.assertEqual(0, installed.returncode, installed.stdout + installed.stderr)
                root = data / "Flevert"
                sentinel = root / "outside-sentinel"
                sentinel.write_text("outside the owned app")
                manifest = root / ".flevert-install.json"
                # The original shell installer wrote no manifest; still exercise its unsafe removal when one is supplied.
                document = json.loads(manifest.read_text()) if manifest.exists() else {"version": 1, "entries": {}}
                document["entries"]["../outside-sentinel"] = "file"
                manifest.write_text(json.dumps(document))
                before_executable = (root / "app" / "flevert").read_bytes()
                before_calls = self.calls.read_text()

                run = self.run_install(*arguments, data_home=data)

                self.assertNotEqual(0, run.returncode, run.stdout + run.stderr)
                self.assertEqual("outside the owned app", sentinel.read_text())
                self.assertEqual(before_executable, (root / "app" / "flevert").read_bytes())
                self.assertEqual(before_calls, self.calls.read_text())

    def test_malformed_manifest_does_not_authorize_update_or_uninstall(self):
        for name, arguments in (("install", ()), ("uninstall", ("--uninstall",))):
            with self.subTest(action=name):
                data = self.dir / f"malformed-data-{name}"
                installed = self.run_install(data_home=data)
                self.assertEqual(0, installed.returncode, installed.stdout + installed.stderr)
                root = data / "Flevert"
                manifest = root / ".flevert-install.json"
                manifest.write_text("{not valid JSON")
                before_executable = (root / "app" / "flevert").read_bytes()
                before_calls = self.calls.read_text()
                new_name = f"new-{name}.txt"
                (self.package / new_name).write_text("new release")

                run = self.run_install(*arguments, data_home=data)

                self.assertNotEqual(0, run.returncode, run.stdout + run.stderr)
                self.assertEqual("{not valid JSON", manifest.read_text())
                self.assertEqual(before_executable, (root / "app" / "flevert").read_bytes())
                self.assertFalse((root / "app" / new_name).exists())
                self.assertEqual(before_calls, self.calls.read_text())

    def test_failed_menu_registration_restores_the_previous_app_and_manifest(self):
        (self.package / "release.txt").write_text("old release")
        installed = self.run_install()
        self.assertEqual(0, installed.returncode, installed.stdout + installed.stderr)
        root = self.data / "Flevert"
        app = root / "app"
        before_executable = (app / "flevert").read_bytes()
        manifest = root / ".flevert-install.json"
        before_manifest = manifest.read_bytes() if manifest.exists() else None
        (self.package / "release.txt").write_text("new release")
        (self.package / "new.txt").write_text("new file")
        (self.package / "flevert").write_text(
            f'#!/bin/sh\necho "$(dirname "$0") $*" >> \'{self.calls}\'\n'
            'if [ "$1" = "install-menu" ]; then exit 1; fi\n')

        run = self.run_install()

        self.assertNotEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertEqual("old release", (app / "release.txt").read_text())
        self.assertEqual(before_executable, (app / "flevert").read_bytes())
        if before_manifest is not None:
            self.assertEqual(before_manifest, manifest.read_bytes())
        self.assertFalse((app / "new.txt").exists())
        self.assertFalse(any(entry.name.startswith(("app.new.", "app.old.")) for entry in root.iterdir()))


if __name__ == "__main__":
    unittest.main()
