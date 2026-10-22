#!/usr/bin/env python3
"""Fetches the third-party binaries the Linux build ships (pdfium, qpdf) and their licences into deps/, each checked
against the SHA256 pinned in scripts/deps-linux.json before anything is unpacked. The Linux counterpart of
fetch-deps.ps1 (ADR-0008), with the same guarantees:

- Nothing in deps/ is committed. Every download goes into a fresh private temporary folder; deps/ is only changed after
  every item has been downloaded, verified and unpacked, so a failure leaves it as it was.
- deps/.fetched records the manifest's hash and the SHA256 of every file kept; a run downloads nothing only when the
  manifest is unchanged and deps/ holds exactly those files, unchanged.
- Only the files a manifest entry lists are taken out of an archive, by name, so an entry naming a path outside the
  archive is never written. A kept symbolic link becomes a copy of the archive's own file it names: deps/ holds no links.

Run with the system Python, isolated: python3 -I scripts/fetch-deps-linux.py
"""

import argparse
import hashlib
import shutil
import stat
import sys
import tarfile
import tempfile
import urllib.parse
import urllib.request
import zipfile
from fnmatch import fnmatchcase
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parent.parent


class FetchError(Exception):
    pass


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def tree_files(base, folder):
    """Every file under base/folder, as a path relative to base. Fails on a symbolic link instead of following it:
    nothing this script writes is one, so it was put there by something else."""
    root = base / folder
    if root.is_symlink():
        raise FetchError(f"{root} is a link.")
    if not root.is_dir():
        return []
    found = []
    for entry in sorted(root.iterdir()):
        if entry.is_symlink():
            raise FetchError(f"{entry} is a link.")
        relative = f"{folder}/{entry.name}"
        found += tree_files(base, relative) if entry.is_dir() else [relative]
    return found


def difference(destination, stamp, manifest_hash):
    """Why deps/ is not up to date, or None when it is."""
    if not stamp.is_file():
        return "no earlier fetch"
    lines = stamp.read_text().splitlines()
    if len(lines) < 2 or lines[0] != manifest_hash:
        return "scripts/deps-linux.json changed"
    recorded = {}
    for line in lines[1:]:
        digest, sep, name = line.partition("  ")
        if not sep or len(digest) != 64:
            return "the stamp is damaged"
        recorded[name] = digest
    try:
        present = [f for folder in sorted({n.split("/")[0] for n in recorded}) for f in tree_files(destination, folder)]
    except FetchError as e:
        return str(e)
    for name in present:
        if name not in recorded:
            return f"{name} is not from the last fetch"
    for name, digest in recorded.items():
        path = destination / name
        if not path.is_file():
            return f"{name} is missing"
        if sha256_of(path) != digest:
            return f"{name} changed"
    return None


def download(item, target):
    url = item["url"]
    scheme = urllib.parse.urlparse(url).scheme
    # Tests point the manifest at local files; real downloads are https (test_the_real_manifest_pins_…).
    if scheme == "file":
        shutil.copyfile(urllib.request.url2pathname(urllib.parse.urlparse(url).path), target)
    elif scheme == "https":
        with urllib.request.urlopen(url, timeout=120) as response, open(target, "xb") as out:
            shutil.copyfileobj(response, out)
    else:
        raise FetchError(f"{item['name']}: only https downloads are fetched, not {url}.")


def relative_name(name, strip):
    """The member's path below strip, or None when it is outside it or not a plain relative path."""
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts:
        return None
    text = str(path)
    if strip:
        prefix = strip.rstrip("/") + "/"
        if not text.startswith(prefix):
            return None
        text = text[len(prefix):]
    return text or None


def matches(relative, pattern):
    """Whole-path match, one path segment per pattern segment, so licenses/* never reaches licenses/a/b."""
    parts, wanted = relative.split("/"), pattern.split("/")
    return len(parts) == len(wanted) and all(fnmatchcase(p, w) for p, w in zip(parts, wanted))


class Archive:
    """Reads members by name from a zip or gzipped tar opened from an already-verified file object."""

    def __init__(self, kind, stream):
        self.kind = kind
        if kind == "zip":
            self._zip = zipfile.ZipFile(stream)
            self.members = {i.filename.rstrip("/"): i for i in self._zip.infolist()}
        elif kind == "tgz":
            self._tar = tarfile.open(fileobj=stream, mode="r:gz")
            self.members = {m.name.rstrip("/"): m for m in self._tar.getmembers()}
        else:
            raise FetchError(f"Unknown kind '{kind}'.")

    def _mode(self, member):
        if self.kind == "zip":
            return member.external_attr >> 16
        mode = member.mode
        if member.issym():
            mode |= stat.S_IFLNK
        elif member.isfile():
            mode |= stat.S_IFREG
        else:
            mode |= stat.S_IFDIR if member.isdir() else 0
        return mode

    def _link_target(self, member):
        if self.kind == "zip":
            return self._zip.read(member).decode()
        return member.linkname

    def read(self, name, hops=0):
        """(bytes, executable) of a regular member; a symbolic link is followed only to another member."""
        member = self.members[name]
        mode = self._mode(member)
        if stat.S_ISLNK(mode):
            target = PurePosixPath(name).parent / self._link_target(member)
            resolved = relative_name(str(target), "")
            if hops > 8 or resolved is None or PurePosixPath(self._link_target(member)).is_absolute() \
                    or resolved not in self.members:
                raise FetchError(f"{name} is a link to something outside the download.")
            return self.read(resolved, hops + 1)
        if self.kind == "zip":
            if stat.S_IFMT(mode) not in (0, stat.S_IFREG) or member.is_dir():
                raise FetchError(f"{name} is not a plain file.")
            return self._zip.read(member), bool(mode & 0o111)
        if not member.isfile():
            raise FetchError(f"{name} is not a plain file.")
        return self._tar.extractfile(member).read(), bool(member.mode & 0o111)


def stage_item(item, download_path, stage):
    with open(download_path, "rb") as held:
        actual = hashlib.sha256(held.read()).hexdigest()
        if actual != item["sha256"]:
            raise FetchError(f"SHA256 mismatch for {item['name']}: expected {item['sha256']}, got {actual}.")
        held.seek(0)

        if item["kind"] == "file":
            target = stage / item["target"]
            target.parent.mkdir(parents=True, exist_ok=True)
            with open(target, "xb") as out:
                shutil.copyfileobj(held, out)
            return

        archive = Archive(item["kind"], held)
        below = {}
        for name in archive.members:
            relative = relative_name(name, item.get("strip", ""))
            if relative is not None:
                below[relative] = name
        for pattern in item["keep"]:
            chosen = sorted(r for r in below if matches(r, pattern))
            if not chosen:
                raise FetchError(f"{item['name']}: nothing matches {pattern} in the download.")
            for relative in chosen:
                data, executable = archive.read(below[relative])
                target = stage / item["name"] / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                with open(target, "xb") as out:
                    out.write(data)
                target.chmod(0o755 if executable else 0o644)


def remove(path):
    """Removes a file or folder without following links: a link is removed itself, never what it points to."""
    if path.is_symlink() or path.is_file():
        path.unlink()
    elif path.is_dir():
        shutil.rmtree(path)  # does not follow links inside (fd-based on Linux)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--manifest", type=Path, default=ROOT / "scripts" / "deps-linux.json")
    parser.add_argument("--destination", type=Path, default=ROOT / "deps")
    args = parser.parse_args()
    manifest, destination = args.manifest.resolve(), args.destination.absolute()

    import json  # after argument parsing, so --help works without reading anything
    manifest_hash = sha256_of(manifest)
    items = json.loads(manifest.read_text())["items"]
    stamp = destination / ".fetched"

    why = difference(destination, stamp, manifest_hash)
    if why is None:
        print("deps are up to date")
        return 0
    print(f"Fetching deps: {why}.")

    # mkdtemp creates the folder 0700, so other accounts cannot swap a download after its hash check.
    work = Path(tempfile.mkdtemp(prefix="flevert-deps-"))
    try:
        stage = work / "stage"
        stage.mkdir()
        for item in items:
            download_path = work / f"{item['name']}.{item['kind']}"
            download(item, download_path)
            stage_item(item, download_path, stage)

        # Everything is verified and unpacked: only now replace what deps/ held for these names.
        folders = sorted(p.name for p in stage.iterdir() if p.is_dir())
        kept = {f: sha256_of(stage / f) for folder in folders for f in tree_files(stage, folder)}
        destination.mkdir(parents=True, exist_ok=True)
        for folder in folders:
            remove(destination / folder)
            shutil.copytree(stage / folder, destination / folder, symlinks=True)
        copied = [f for folder in folders for f in tree_files(destination, folder)]
        changed = [f for f in kept if f not in copied or sha256_of(destination / f) != kept[f]]
        changed += [f for f in copied if f not in kept]
        if changed:
            raise FetchError(f"{', '.join(changed)} in {destination} changed while being copied; run this script again.")
        stamp.write_text("\n".join([manifest_hash] + [f"{kept[f]}  {f}" for f in sorted(kept)]) + "\n")
        print(f"deps fetched into {destination}")
        return 0
    except (FetchError, OSError, zipfile.BadZipFile, tarfile.TarError, KeyError, ValueError) as e:
        print(f"{e} Nothing was changed in {destination}." if isinstance(e, FetchError) else f"{e!r}", file=sys.stderr)
        return 1
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
