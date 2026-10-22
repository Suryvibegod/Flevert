"""Tests for flevert_menu.py, the Nautilus extension, with a stand-in for nautilus-python (gi.repository.Nautilus) in
both of its API versions: 3.0 (Nautilus 42 and older: get_file_items(window, files)) and 4.0 (Nautilus 43 and later:
get_file_items(files)).

Run: python3 -I -m unittest discover -s src/Flevert.Linux/nautilus -p 'test_*.py'
"""

import importlib
import json
import os
import sys
import tempfile
import types
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent


class FakeMenu:
    def __init__(self):
        self.items = []

    def append_item(self, item):
        self.items.append(item)


class FakeMenuItem:
    def __init__(self, name=None, label=None, **_):
        self.name, self.label, self.submenu, self.handlers = name, label, None, []

    def set_submenu(self, menu):
        self.submenu = menu

    def connect(self, signal, handler, *data):
        self.handlers.append((signal, handler, data))

    def activate(self):
        for signal, handler, data in self.handlers:
            if signal == "activate":
                handler(self, *data)


class FakeLocation:
    def __init__(self, path):
        self._path = path

    def get_path(self):
        return self._path


class FakeFile:
    def __init__(self, path, directory=False):
        self._path, self._directory = path, directory

    def get_location(self):
        return FakeLocation(self._path)

    def is_directory(self):
        return self._directory


def install_fake_gi(version):
    gi = types.ModuleType("gi")
    loaded = {}

    def require_version(name, wanted):
        if name == "Nautilus" and wanted != version:
            raise ValueError(f"Namespace Nautilus not available for version {wanted}")
        loaded[name] = wanted

    gi.require_version = require_version
    repository = types.ModuleType("gi.repository")
    nautilus = types.ModuleType("gi.repository.Nautilus")
    nautilus.Menu, nautilus.MenuItem = FakeMenu, FakeMenuItem
    nautilus.MenuProvider = type("MenuProvider", (), {})
    gobject = types.ModuleType("gi.repository.GObject")
    gobject.GObject = type("GObject", (), {})
    repository.Nautilus, repository.GObject = nautilus, gobject
    gi.repository = repository
    sys.modules.update({"gi": gi, "gi.repository": repository, "gi.repository.Nautilus": nautilus,
                        "gi.repository.GObject": gobject})


MENU = {
    "version": 1,
    "maxFiles": 1000,
    "operations": {
        "image.to-jpg": [".png", ".heic"],
        "image.to-png": [".jpg", ".heic"],
        "image.compress": [".jpg", ".png", ".heic"],
        "pdf.merge": [".pdf"],
        "pdf.blank-pages": [".pdf"],
    },
    "menus": {
        ".heic": [{"label": "Convert to", "children": [{"label": "JPG", "op": "image.to-jpg"}, {"label": "PNG", "op": "image.to-png"}]},
                  {"label": "Compress", "op": "image.compress"}],
        ".png": [{"label": "Convert to", "children": [{"label": "JPG", "op": "image.to-jpg"}]},
                 {"label": "Compress", "op": "image.compress"}],
        ".jpg": [{"label": "Convert to", "children": [{"label": "PNG", "op": "image.to-png"}]},
                 {"label": "Compress", "op": "image.compress"}],
        ".pdf": [{"label": "Merge PDFs", "op": "pdf.merge"},
                 {"label": "More PDF tools", "children": [{"label": "Remove blank pages", "op": "pdf.blank-pages"}]}],
    },
}


class FlevertMenuTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)
        self.flevert = self.dir / "flevert"
        self.flevert.write_text("#!/bin/sh\n")
        self.flevert.chmod(0o755)
        self.menu_path = self.dir / "menu.json"
        self.write_menu(MENU)
        self.started = []

    def tearDown(self):
        self._tmp.cleanup()
        for name in ["flevert_menu", "gi", "gi.repository", "gi.repository.Nautilus", "gi.repository.GObject"]:
            sys.modules.pop(name, None)

    def write_menu(self, menu, flevert=None):
        self.menu_path.write_text(json.dumps(dict(menu, flevert=str(flevert or self.flevert))))

    def load(self, version="4.0"):
        install_fake_gi(version)
        sys.path.insert(0, str(HERE))
        try:
            module = importlib.import_module("flevert_menu")
        finally:
            sys.path.remove(str(HERE))
        module.MENU_FILE = str(self.menu_path)
        module.start = lambda arguments: self.started.append(arguments)
        return module

    def items(self, module, paths, version="4.0", directory=False):
        files = [FakeFile(path, directory) for path in paths]
        provider = module.FlevertMenu()
        return provider.get_file_items(files) if version == "4.0" else provider.get_file_items(object(), files)

    @staticmethod
    def tree(item):
        if item.submenu is None:
            return item.label
        return {item.label: [FlevertMenuTests.tree(child) for child in item.submenu.items]}

    def test_a_photo_gets_the_flevert_menu_of_its_type(self):
        module = self.load()

        items = self.items(module, ["/home/u/IMG_1.heic"])

        self.assertEqual([{"Flevert": [{"Convert to": ["JPG", "PNG"]}, "Compress"]}], [self.tree(i) for i in items])

    def test_nautilus_42_calls_with_the_window_first(self):
        module = self.load("3.0")

        items = self.items(module, ["/home/u/IMG_1.heic"], version="3.0")

        self.assertEqual(["Flevert"], [i.label for i in items])

    def test_a_pdf_gets_more_pdf_tools_as_a_submenu(self):
        module = self.load()

        items = self.items(module, ["/home/u/a.pdf"])

        self.assertEqual([{"Flevert": ["Merge PDFs", {"More PDF tools": ["Remove blank pages"]}]}], [self.tree(i) for i in items])

    def test_a_mixed_selection_keeps_only_what_applies_to_every_file(self):
        module = self.load()

        items = self.items(module, ["/home/u/a.heic", "/home/u/b.png"])

        # PNG cannot become PNG, so only JPG is left in Convert to; Compress applies to both.
        self.assertEqual([{"Flevert": [{"Convert to": ["JPG"]}, "Compress"]}], [self.tree(i) for i in items])

    def test_nothing_in_common_means_no_menu(self):
        module = self.load()

        self.assertEqual([], self.items(module, ["/home/u/a.pdf", "/home/u/b.png"]))

    def test_a_type_flevert_does_not_handle_gets_no_menu(self):
        module = self.load()

        self.assertEqual([], self.items(module, ["/home/u/notes.txt"]))

    def test_the_extension_is_matched_whatever_its_case(self):
        module = self.load()

        self.assertEqual(["Flevert"], [i.label for i in self.items(module, ["/home/u/IMG_1.HEIC"])])

    def test_a_folder_or_a_remote_file_gets_no_menu(self):
        module = self.load()

        self.assertEqual([], self.items(module, ["/home/u/album.heic"], directory=True))
        self.assertEqual([], self.items(module, [None]))

    def test_choosing_an_entry_runs_flevert_once_with_every_selected_file(self):
        module = self.load()
        items = self.items(module, ["/home/u/b 2.heic", "/home/u/a.heic"])

        items[0].submenu.items[0].submenu.items[0].activate()  # Convert to > JPG

        self.assertEqual([[str(self.flevert), "run", "image.to-jpg", "/home/u/b 2.heic", "/home/u/a.heic"]], self.started)

    def test_more_files_than_flevert_takes_reach_it_as_one_too_many_so_it_says_so(self):
        # Thousands of paths could pass what one command line holds, and nothing would happen at all.
        self.write_menu(dict(MENU, maxFiles=2))
        module = self.load()
        paths = [f"/home/u/{n}.heic" for n in range(5)]

        self.items(module, paths)[0].submenu.items[1].activate()  # Compress

        self.assertEqual([[str(self.flevert), "run", "image.compress", *paths[:3]]], self.started)

    def test_a_damaged_entry_gives_no_menu_and_no_error(self):
        damaged = [
            [{"label": 5, "op": "image.compress"}],
            [{"label": "Compress", "op": ["image.compress"]}],
            [{"label": "Convert to", "children": "JPG"}],
            [{"label": "Convert to"}],
            ["Compress"],
            {"label": "Compress", "op": "image.compress"},
        ]
        for entries in damaged:
            with self.subTest(entries=entries):
                self.write_menu(dict(MENU, menus={".heic": entries}))
                module = self.load()

                self.assertEqual([], self.items(module, ["/home/u/a.heic"]))
            self.tearDown_modules()

        for menu in [dict(MENU, maxFiles=0), dict(MENU, maxFiles="1000"), dict(MENU, operations={"image.compress": ".heic"})]:
            with self.subTest(menu=menu):
                self.write_menu(menu)
                module = self.load()

                self.assertEqual([], self.items(module, ["/home/u/a.heic"]))
            self.tearDown_modules()

    def test_a_flevert_that_cannot_start_raises_nothing_inside_nautilus(self):
        module = self.load()
        del module.start  # the module's own
        module = importlib.reload(module)

        module.start([str(self.dir / "missing"), "run", "x", "/home/u/a.heic"])

    def test_a_menu_file_that_cannot_be_read_gives_no_menu_and_no_error(self):
        module = self.load()
        self.menu_path.write_text("{ not json")

        self.assertEqual([], self.items(module, ["/home/u/a.heic"]))

    def test_a_flevert_that_is_not_an_absolute_executable_file_gives_no_menu(self):
        for flevert in ["flevert", str(self.dir / "missing"), str(self.dir)]:
            with self.subTest(flevert=flevert):
                module = self.load()
                self.menu_path.write_text(json.dumps(dict(MENU, flevert=flevert)))

                self.assertEqual([], self.items(module, ["/home/u/a.heic"]))
            self.tearDown_modules()

    def tearDown_modules(self):
        for name in ["flevert_menu", "gi", "gi.repository", "gi.repository.Nautilus", "gi.repository.GObject"]:
            sys.modules.pop(name, None)

    def test_the_real_start_runs_flevert_without_a_shell_and_without_waiting(self):
        module = self.load()
        del module.start  # the module's own
        module = importlib.reload(module)
        marker = self.dir / "ran"
        script = self.dir / "fake-flevert"
        script.write_text(f"#!/bin/sh\necho \"$@\" > '{marker}'\n")
        script.chmod(0o755)

        module.start([str(script), "run", "x", "/home/u/a;rm -rf ~.heic"])

        for _ in range(200):
            if marker.exists() and marker.read_text():
                break
            import time
            time.sleep(0.02)
        self.assertEqual("run x /home/u/a;rm -rf ~.heic\n", marker.read_text())


if __name__ == "__main__":
    unittest.main()
