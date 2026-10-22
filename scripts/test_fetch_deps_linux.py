"""Tests for scripts/fetch-deps-linux.py, the only way pdfium and qpdf reach a Linux build: it must check every download
against its pinned hash before unpacking anything, keep only the listed files, and leave deps/ untouched on any failure.

Run: python3 -m unittest discover -s scripts -p 'test_*.py'
"""

import hashlib
import io
import json
import os
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
SCRIPT = SCRIPTS / "fetch-deps-linux.py"


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def files(folder):
    """Every file under folder, relative, sorted."""
    folder = Path(folder)
    return sorted(str(p.relative_to(folder)) for p in folder.rglob("*") if p.is_file() or p.is_symlink())


class FetchDepsLinuxTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)
        self.deps = self.dir / "deps"

    def tearDown(self):
        self._tmp.cleanup()

    # Helpers -------------------------------------------------------------------------------------------------------

    def zip(self, *entries, name="tool.zip"):
        path = self.dir / name
        with zipfile.ZipFile(path, "w") as archive:
            for entry_name, text, *mode in entries:
                info = zipfile.ZipInfo(entry_name)
                info.external_attr = (mode[0] if mode else 0o100644) << 16
                archive.writestr(info, text)
        return path

    def tgz(self, *entries, name="tool.tgz"):
        """entries: (name, text, mode) for files, (name, None, link_target) for symbolic links."""
        path = self.dir / name
        with tarfile.open(path, "w:gz") as archive:
            for entry_name, text, extra in entries:
                info = tarfile.TarInfo(entry_name)
                if text is None:
                    info.type = tarfile.SYMTYPE
                    info.linkname = extra
                    archive.addfile(info)
                else:
                    data = text.encode()
                    info.size = len(data)
                    info.mode = extra
                    archive.addfile(info, io.BytesIO(data))
        return path

    def text_file(self, name, text):
        path = self.dir / name
        path.write_text(text)
        return path

    @staticmethod
    def item(name, source, digest, kind, strip="", keep=None, target=None):
        entry = {"name": name, "url": Path(source).as_uri(), "sha256": digest, "kind": kind}
        if kind == "file":
            entry["target"] = target
        else:
            entry["strip"] = strip
            entry["keep"] = keep
        return entry

    def manifest(self, *items):
        path = self.dir / "deps-linux.json"
        path.write_text(json.dumps({"items": list(items)}))
        return path

    def fetch(self, manifest):
        return subprocess.run(
            [sys.executable, "-I", str(SCRIPT), "--manifest", str(manifest), "--destination", str(self.deps)],
            capture_output=True, text=True, timeout=120)

    # Tests ---------------------------------------------------------------------------------------------------------

    def test_the_real_manifest_pins_every_download_by_sha256_over_https(self):
        items = json.loads((SCRIPTS / "deps-linux.json").read_text())["items"]

        self.assertTrue(items)
        for item in items:
            self.assertTrue(item["url"].startswith("https://"), item["url"])
            self.assertRegex(item["sha256"], "^[0-9a-f]{64}$")

    def test_a_download_with_the_pinned_hash_is_unpacked_keeping_only_the_listed_files(self):
        archive = self.zip(("tool-1.0/bin/tool", "exe", 0o100755), ("tool-1.0/lib/libtool.so.1", "so"),
                           ("tool-1.0/include/tool.h", "h"))
        licence = self.text_file("LICENSE.txt", "licence text")
        manifest = self.manifest(
            self.item("tool", archive, sha256(archive), "zip", strip="tool-1.0/", keep=["bin/tool", "lib/*.so.1"]),
            self.item("tool-licence", licence, sha256(licence), "file", target="tool/LICENSE.txt"))

        run = self.fetch(manifest)

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertEqual(["LICENSE.txt", "bin/tool", "lib/libtool.so.1"], files(self.deps / "tool"))
        self.assertEqual("exe", (self.deps / "tool/bin/tool").read_text())

    def test_an_executable_in_the_archive_stays_executable_and_nothing_else_becomes_one(self):
        archive = self.tgz(("bin/tool", "exe", 0o755), ("LICENSE", "text", 0o644))
        manifest = self.manifest(self.item("tool", archive, sha256(archive), "tgz", keep=["bin/tool", "LICENSE"]))

        run = self.fetch(manifest)

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertTrue(os.access(self.deps / "tool/bin/tool", os.X_OK))
        self.assertFalse(os.access(self.deps / "tool/LICENSE", os.X_OK))

    def test_a_kept_symbolic_link_in_the_archive_becomes_a_copy_of_the_file_it_names(self):
        archive = self.tgz(("lib/libtool.so.1.2.3", "so", 0o644), ("lib/libtool.so.1", None, "libtool.so.1.2.3"))
        manifest = self.manifest(self.item("tool", archive, sha256(archive), "tgz", keep=["lib/libtool.so.1"]))

        run = self.fetch(manifest)

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        kept = self.deps / "tool/lib/libtool.so.1"
        self.assertFalse(kept.is_symlink())
        self.assertEqual("so", kept.read_text())

    def test_a_symbolic_link_in_the_archive_pointing_outside_it_fails(self):
        archive = self.tgz(("lib/libtool.so.1", None, "/etc/passwd"))
        manifest = self.manifest(self.item("tool", archive, sha256(archive), "tgz", keep=["lib/libtool.so.1"]))

        run = self.fetch(manifest)

        self.assertNotEqual(0, run.returncode)
        self.assertFalse((self.deps / "tool").exists())

    def test_an_entry_whose_name_climbs_out_of_the_archive_is_never_written(self):
        archive = self.tgz(("../escaped", "x", 0o644), ("bin/tool", "exe", 0o755))
        manifest = self.manifest(self.item("tool", archive, sha256(archive), "tgz", keep=["*", "bin/tool"]))

        self.fetch(manifest)

        self.assertFalse((self.dir / "escaped").exists())
        self.assertFalse((self.deps / "escaped").exists())

    def test_a_download_whose_hash_differs_fails_and_changes_nothing(self):
        archive = self.zip(("bin/tool", "exe"))
        (self.deps / "tool").mkdir(parents=True)
        (self.deps / "tool/old.txt").write_text("from an earlier fetch")
        manifest = self.manifest(self.item("tool", archive, "0" * 64, "zip", keep=["bin/tool"]))

        run = self.fetch(manifest)

        self.assertNotEqual(0, run.returncode)
        self.assertIn("SHA256", run.stdout + run.stderr)
        self.assertEqual(["old.txt"], files(self.deps / "tool"))

    def test_a_listed_file_missing_from_the_archive_fails(self):
        archive = self.zip(("bin/tool", "exe"))
        manifest = self.manifest(self.item("tool", archive, sha256(archive), "zip", keep=["bin/tool", "lib/missing.so"]))

        run = self.fetch(manifest)

        self.assertNotEqual(0, run.returncode)
        self.assertIn("lib/missing.so", run.stdout + run.stderr)
        self.assertFalse((self.deps / "tool").exists())

    def test_a_second_run_with_nothing_changed_downloads_nothing(self):
        archive = self.zip(("bin/tool", "exe"))
        manifest = self.manifest(self.item("tool", archive, sha256(archive), "zip", keep=["bin/tool"]))
        self.assertEqual(0, self.fetch(manifest).returncode)
        archive.unlink()  # a download now would fail

        run = self.fetch(manifest)

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertIn("up to date", run.stdout)

    def test_a_deleted_dependency_is_fetched_again(self):
        archive = self.zip(("bin/tool", "exe"))
        manifest = self.manifest(self.item("tool", archive, sha256(archive), "zip", keep=["bin/tool"]))
        self.assertEqual(0, self.fetch(manifest).returncode)
        (self.deps / "tool/bin/tool").unlink()

        run = self.fetch(manifest)

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertEqual("exe", (self.deps / "tool/bin/tool").read_text())

    def test_a_kept_file_changed_after_the_fetch_is_fetched_again(self):
        archive = self.zip(("bin/tool", "exe"))
        manifest = self.manifest(self.item("tool", archive, sha256(archive), "zip", keep=["bin/tool"]))
        self.assertEqual(0, self.fetch(manifest).returncode)
        (self.deps / "tool/bin/tool").write_text("tampered")

        run = self.fetch(manifest)

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertEqual("exe", (self.deps / "tool/bin/tool").read_text())

    def test_a_file_added_beside_the_kept_files_is_removed_by_fetching_again(self):
        archive = self.zip(("bin/tool", "exe"))
        manifest = self.manifest(self.item("tool", archive, sha256(archive), "zip", keep=["bin/tool"]))
        self.assertEqual(0, self.fetch(manifest).returncode)
        (self.deps / "tool/bin/planted.so").write_text("planted")

        run = self.fetch(manifest)

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertEqual(["bin/tool"], files(self.deps / "tool"))

    def test_fetching_again_removes_a_symbolic_link_in_deps_without_touching_what_it_points_to(self):
        archive = self.zip(("bin/tool", "exe"))
        manifest = self.manifest(self.item("tool", archive, sha256(archive), "zip", keep=["bin/tool"]))
        self.assertEqual(0, self.fetch(manifest).returncode)
        outside = self.dir / "outside"
        outside.mkdir()
        (outside / "precious.txt").write_text("keep me")
        (self.deps / "tool/bin/link").symlink_to(outside)

        run = self.fetch(manifest)

        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertEqual(["bin/tool"], files(self.deps / "tool"))
        self.assertEqual("keep me", (outside / "precious.txt").read_text())

    def test_only_https_and_local_files_are_downloaded(self):
        manifest = self.manifest({"name": "tool", "url": "http://example.invalid/tool.zip", "sha256": "0" * 64,
                                  "kind": "zip", "strip": "", "keep": ["bin/tool"]})

        run = self.fetch(manifest)

        self.assertNotEqual(0, run.returncode)
        self.assertIn("https", run.stdout + run.stderr)


if __name__ == "__main__":
    unittest.main()
