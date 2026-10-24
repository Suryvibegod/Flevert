# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Flevert contributors
"""Flevert in the Nautilus right-click menu.

`flevert install-menu` puts this file in ~/.local/share/nautilus-python/extensions/ and writes the menu it shows,
menu.json, in Flevert's own folder: one tree of entries for each file extension, as the Windows menu has one for each,
and the extensions each operation takes. The menu is read again on every right-click, so a reinstall needs no restart
of Nautilus. Choosing an entry starts flevert once with every selected file (docs/specs/linux.md).

Works with nautilus-python's API 4.0 (Nautilus 43 and later) and 3.0 (Nautilus 42 and older).
"""

import json
import os
import subprocess
import sys
import threading

import gi

try:
    gi.require_version("Nautilus", "4.0")
except ValueError:
    gi.require_version("Nautilus", "3.0")
from gi.repository import GObject, Nautilus  # noqa: E402

# Where flevert keeps its own files: .NET's LocalApplicationData on Linux.
MENU_FILE = os.path.join(os.environ.get("XDG_DATA_HOME") or os.path.expanduser("~/.local/share"), "Flevert", "menu.json")
MAX_MENU_BYTES = 1 << 20


def start(arguments):
    """Starts flevert without a shell and without waiting for it; a thread collects it when it ends. Nothing it raises
    reaches Nautilus: a flevert that cannot start is told to Nautilus's own log."""
    try:
        process = subprocess.Popen(arguments, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                   stderr=subprocess.DEVNULL, close_fds=True, start_new_session=True)
    except OSError as error:
        print(f"Flevert could not start {arguments[0]}: {error}", file=sys.stderr)
        return
    threading.Thread(target=process.wait, daemon=True).start()


def valid_entries(entries):
    """Whether entries is a list of menu entries: each with a text label, and an operation or a list of entries."""
    if not isinstance(entries, list):
        return False
    for entry in entries:
        if not isinstance(entry, dict) or not isinstance(entry.get("label"), str):
            return False
        if "op" in entry:
            if not isinstance(entry["op"], str):
                return False
        elif not valid_entries(entry.get("children")) or not entry["children"]:
            return False
    return True


def valid_operations(operations):
    """Whether operations maps each operation to the list of extensions it takes."""
    return isinstance(operations, dict) and all(
        isinstance(extensions, list) and all(isinstance(e, str) for e in extensions) for extensions in operations.values())


def load_menu():
    """menu.json, or None when it is missing, damaged or names no flevert that can run."""
    try:
        with open(MENU_FILE, "rb") as stream:
            data = stream.read(MAX_MENU_BYTES + 1)
        if len(data) > MAX_MENU_BYTES:
            return None
        menu = json.loads(data)
        flevert = menu["flevert"]
        if not (isinstance(flevert, str) and os.path.isabs(flevert) and os.path.isfile(flevert)
                and os.access(flevert, os.X_OK)):
            return None
        if not isinstance(menu.get("menus"), dict) or not valid_operations(menu.get("operations")):
            return None
        if not all(valid_entries(entries) for entries in menu["menus"].values()):
            return None
        max_files = menu.get("maxFiles")
        if not isinstance(max_files, int) or isinstance(max_files, bool) or max_files < 1:
            return None
        return menu
    except (OSError, ValueError, KeyError, TypeError):
        return None


def selected_paths(files):
    """The local paths of the selected files, or None when anything selected is a folder or not a local file."""
    paths = []
    for file in files:
        if file.is_directory():
            return None
        location = file.get_location()
        path = location.get_path() if location is not None else None
        if not path:
            return None
        paths.append(path)
    return paths or None


def kept(nodes, operations, extensions):
    """The entries whose operation takes every extension selected; submenus left empty go too."""
    result = []
    for node in nodes:
        if "op" in node:
            if extensions <= {e.lower() for e in operations.get(node["op"], ())}:
                result.append(node)
        else:
            children = kept(node.get("children", []), operations, extensions)
            if children:
                result.append(dict(node, children=children))
    return result


class FlevertMenu(GObject.GObject, Nautilus.MenuProvider):
    def get_file_items(self, *args):
        # API 4.0 passes the files only; 3.0 passes the window first.
        paths = selected_paths(args[-1])
        if paths is None:
            return []
        menu = load_menu()
        if menu is None:
            return []
        extensions = [os.path.splitext(path)[1].lower() for path in paths]
        # The menu of the first file's type, as Explorer shows the menu of the file clicked.
        entries = kept(menu["menus"].get(extensions[0], []), menu["operations"], set(extensions))
        if not entries:
            return []

        top = Nautilus.MenuItem(name="Flevert::Flevert", label="Flevert")
        # More files than flevert takes reach it as one too many, so it says so: thousands of paths could pass what one
        # command line holds, and then nothing would happen at all.
        paths = paths[:menu["maxFiles"] + 1]
        top.set_submenu(self._submenu(entries, "Flevert", menu["flevert"], paths))
        return [top]

    def _submenu(self, entries, path, flevert, paths):
        submenu = Nautilus.Menu()
        for entry in entries:
            name = f"{path}/{entry['label']}"
            item = Nautilus.MenuItem(name="Flevert::" + name, label=entry["label"])
            if "op" in entry:
                item.connect("activate", self._run, flevert, entry["op"], paths)
            else:
                item.set_submenu(self._submenu(entry["children"], name, flevert, paths))
            submenu.append_item(item)
        return submenu

    @staticmethod
    def _run(_item, flevert, operation, paths):
        start([flevert, "run", operation, *paths])
