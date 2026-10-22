#!/usr/bin/env python3
"""Install Flevert for this user. Only manifest-owned files may be replaced or removed."""

import base64
import json
import fcntl
import os
import shutil
import stat
import subprocess
import sys
import tempfile
from pathlib import Path, PurePosixPath

MANIFEST = ".flevert-install.json"
RUNTIME = ("logs", "magick", "pipes", "update", "media")
REQUIRED = ("flevert", "install.sh", "install.py")
MAX_MANIFEST = 1 << 20


class InstallError(Exception):
    pass


def info(path):
    try:
        return path.lstat()
    except FileNotFoundError:
        return None


def hierarchy(path, create=False):
    """Check components before using them; only ancestors may be sticky and writable."""
    for part in reversed((path, *path.parents)):
        node = info(part)
        if node is None and create:
            part.mkdir(mode=0o700)
            node = part.lstat()
        if node is None:
            continue
        if (not stat.S_ISDIR(node.st_mode) or node.st_uid not in (0, os.geteuid())
                or (node.st_mode & 0o022 and (part == path or not node.st_mode & stat.S_ISVTX))):
            raise InstallError(f"Refusing an unsafe installation path: {part}")


def read_owned(path):
    if info(path) is None:
        return None
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, "rb") as stream:
        node = os.fstat(stream.fileno())
        if (not stat.S_ISREG(node.st_mode) or node.st_uid != os.geteuid() or node.st_nlink != 1
                or node.st_mode & 0o022 or node.st_size > MAX_MANIFEST):
            raise InstallError(f"Not a private file owned by this user: {path}")
        return stream.read(MAX_MANIFEST + 1), stat.S_IMODE(node.st_mode)


def replace_file(path, saved):
    if saved is None:
        path.unlink(missing_ok=True)
        return
    content, mode = saved
    descriptor, name = tempfile.mkstemp(prefix=".flevert-manifest-", dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            os.fchmod(stream.fileno(), mode)
            stream.write(content)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def entries_of(app, harden=False):
    """Inspect directories before descending. No links, special files or files owned by another account."""
    entries = {}
    pending = [app]
    while pending:
        for item in pending.pop().iterdir():
            node = item.lstat()
            kind = "directory" if stat.S_ISDIR(node.st_mode) else "file" if stat.S_ISREG(node.st_mode) else None
            if kind is None or node.st_uid != os.geteuid() or (kind == "file" and node.st_nlink != 1):
                raise InstallError(f"Not an owned regular installation entry: {item}")
            if harden:
                item.chmod((stat.S_IMODE(node.st_mode) | (0o700 if kind == "directory" else 0o600)) & ~0o022)
            elif node.st_mode & 0o022:
                raise InstallError(f"Other accounts can change {item}")
            entries[item.relative_to(app).as_posix()] = kind
            if kind == "directory":
                pending.append(item)
    return entries


def load_manifest(root):
    saved = read_owned(root / MANIFEST)
    if saved is None:
        raise InstallError("This is not a recognized Flevert installation; existing files were left alone.")
    record = json.loads(saved[0])
    if not isinstance(record, dict) or type(record.get("version")) is not int or record["version"] != 1:
        raise InstallError("The Flevert installation manifest is invalid.")
    entries = record.get("entries")
    if not isinstance(entries, dict) or not entries:
        raise InstallError("The Flevert installation manifest is empty.")
    for name, kind in entries.items():
        path = PurePosixPath(name)
        if (not path.parts or "\\" in name or "\0" in name or path.is_absolute() or ".." in path.parts
                or path.as_posix() != name or kind not in ("file", "directory")):
            raise InstallError("The Flevert installation manifest contains an unsafe entry.")
        for parent in path.parents:
            if parent != PurePosixPath(".") and entries.get(parent.as_posix()) != "directory":
                raise InstallError("The Flevert installation manifest has an unrecorded parent.")
    if any(entries.get(name) != "file" for name in REQUIRED):
        raise InstallError("The Flevert installation manifest is incomplete.")
    return entries, saved


def installed_entries(root, allow_unknown):
    entries, saved = load_manifest(root)
    app = root / "app"
    hierarchy(app)
    if info(app) is None or app.lstat().st_uid != os.geteuid():
        raise InstallError("The installed app is missing or not owned by this user.")
    present = entries_of(app)
    if any(name in entries and entries[name] != kind for name, kind in present.items()):
        raise InstallError("An installed entry has been replaced by a different kind of file.")
    if not allow_unknown and present.keys() - entries.keys():
        raise InstallError("The app contains unrecognized files; move them elsewhere before updating Flevert.")
    return entries, saved


def uninstall(root, entries):
    app = root / "app"
    hierarchy(root.parent / "nautilus-python" / "extensions")
    # Check the known runtime folders before removing anything; unknown root entries are never removed.
    for name in RUNTIME:
        if info(root / name) is not None:
            hierarchy(root / name)
            entries_of(root / name)
    subprocess.run([str(app / "flevert"), "uninstall-menu"], check=True)
    for name, kind in sorted(entries.items(), key=lambda pair: len(PurePosixPath(pair[0]).parts), reverse=True):
        path = app / name
        if kind == "file":
            path.unlink(missing_ok=True)
        elif path.is_dir() and not any(path.iterdir()):
            path.rmdir()
    if not any(app.iterdir()):
        app.rmdir()
    for name in RUNTIME:
        if info(root / name) is not None:
            shutil.rmtree(root / name)
    (root / MANIFEST).unlink()
    print("Flevert is removed. Restart Nautilus to see the menu go: nautilus -q")


def install(root, source, old_manifest):
    missing = [tool for tool in ("setsid", "setpriv", "prlimit", "gdbus", "zenity", "fc-match")
               if not any(os.access(folder + tool, os.X_OK) for folder in ("/usr/bin/", "/bin/"))]
    if missing:
        raise InstallError("Flevert needs these programs: " + " ".join(missing)
                           + ". On Ubuntu/Debian: sudo apt install util-linux libglib2.0-bin zenity fontconfig")
    app = root / "app"
    extension = root.parent / "nautilus-python" / "extensions" / "flevert_menu.py"
    hierarchy(extension.parent)
    snapshots = {path: read_owned(path) for path in (root / "menu.json", extension)}
    working = Path(tempfile.mkdtemp(prefix=".flevert-install-", dir=root))
    staging, previous = working / "app", working / "previous"
    completed = False
    published = False
    started = False
    rolled_back = False
    try:
        shutil.copytree(source, staging, symlinks=True)
        staging.chmod(0o700)
        entries = entries_of(staging, harden=True)
        if any(entries.get(name) != "file" for name in REQUIRED):
            raise InstallError("The package is incomplete.")
        # Persist recovery metadata before moving the app. A failed metadata rollback must not lose its only copy.
        saved_state = {"manifest": old_manifest, "menu": snapshots[root / "menu.json"], "extension": snapshots[extension]}
        recovery = {name: None if saved is None else {"content": base64.b64encode(saved[0]).decode("ascii"), "mode": saved[1]}
                    for name, saved in saved_state.items()}
        replace_file(working / ".recovery.json", (json.dumps(recovery).encode("utf-8"), 0o600))
        try:
            started = True
            if info(app) is not None:
                app.rename(previous)
            staging.rename(app)
            published = True
            replace_file(root / MANIFEST, (json.dumps({"version": 1, "entries": entries}).encode("utf-8"), 0o600))
            subprocess.run([str(app / "flevert"), "install-menu"], check=True)
            completed = True
        except BaseException:
            try:
                if published:
                    app.rename(working / "failed")
                if previous.exists():
                    previous.rename(app)
                replace_file(root / MANIFEST, old_manifest)
                for path, saved in snapshots.items():
                    replace_file(path, saved)
                rolled_back = True
            except BaseException as rollback_error:
                raise InstallError(f"Rollback could not finish. Recovery files are kept in {working}") from rollback_error
            raise
    finally:
        # Restore every snapshot before deleting recovery files, even if the prior app has already been put back.
        if completed or rolled_back or not started:
            shutil.rmtree(working)


def main():
    if sys.argv[1:] not in ([], ["--uninstall"]):
        raise InstallError("Usage: install.sh [--uninstall]")
    # Inherited by install-menu, including any Nautilus extension directories it creates.
    os.umask(0o077)
    data = Path(os.environ.get("XDG_DATA_HOME") or str(Path.home() / ".local" / "share"))
    if not data.is_absolute() or ".." in data.parts:
        raise InstallError("XDG_DATA_HOME must be an absolute path without '..'.")
    root = data / "Flevert"
    removing = bool(sys.argv[1:])
    hierarchy(root)
    if removing and info(root) is None:
        return
    hierarchy(root, create=True)
    if root.lstat().st_uid != os.geteuid():
        raise InstallError("The Flevert folder is not owned by this user.")
    # Lock the directory inode itself: no stale lock file after a crash, and no unlink/recreate window between lockers.
    descriptor = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        opened, named = os.fstat(descriptor), root.lstat()
        if (opened.st_dev, opened.st_ino) != (named.st_dev, named.st_ino):
            raise InstallError("The installation folder changed; try again.")
        old_manifest = None
        entries = None
        if info(root / "app") is not None or info(root / MANIFEST) is not None or removing:
            entries, old_manifest = installed_entries(root, allow_unknown=removing)
        root.chmod(0o700)
        if removing:
            uninstall(root, entries)
        else:
            install(root, Path(__file__).resolve().parent, old_manifest)
        if not any(root.iterdir()):
            root.rmdir()
    finally:
        os.close(descriptor)


if __name__ == "__main__":
    try:
        main()
    except (InstallError, OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"Flevert could not finish: {error}", file=sys.stderr)
        sys.exit(1)
