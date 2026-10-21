#!/usr/bin/env python3
"""Writes THIRD-PARTY-NOTICES.txt into the Linux publish folder, and fails rather than let the package ship a file
without its licence: the Linux counterpart of write-notices.ps1, with the same rules.

- Every package and runtime pack in flevert.deps.json is shipped, so each must have a licence or notice file in the NuGet
  packages folder, and all of them go in the notices.
- Every file of the publish folder must be an asset of flevert.deps.json, one of Flevert's own files, or a file of an
  entry under "notices" in the manifest (scripts/deps-linux.json), whose licence files go in the notices too.

Run with the system Python, isolated: python3 -I scripts/write-notices-linux.py --publish <folder> --packages <folder>
"""

import argparse
import glob
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LICENCE_NAMES = {"Notice.txt", "Copyright.txt", "LICENSE.TXT", "LICENSE", "THIRD-PARTY-NOTICES.TXT"}
# Flevert's own (MIT, like the repository): the host and its settings, the empty OpenSSL configuration (ADR-0008), the
# Nautilus extension, the installer script, and what this script and the build add.
OWN = {"flevert", "flevert.deps.json", "flevert.runtimeconfig.json", "THIRD-PARTY-NOTICES.txt", "LICENSE.txt", "openssl.cnf",
       "nautilus/flevert_menu.py", "install.sh"}
RULE = "=" * 78


class NoticeError(Exception):
    pass


def section(title, text, preface=()):
    lines = ["", RULE, title, RULE, *preface]
    if preface:
        lines.append("")
    return "\n".join(lines) + "\n" + text + ("" if text.endswith("\n") else "\n")


def write(publish, packages, manifest, root):
    deps = json.loads((publish / "flevert.deps.json").read_text(encoding="utf-8"))
    notices = ["Flevert includes the following third-party software.\n"]
    for name, library in deps["libraries"].items():
        if library.get("type") == "project":
            continue
        package_id, version = name.removeprefix("runtimepack.").split("/")
        folder = packages / package_id.lower() / version
        files = sorted(f for f in folder.iterdir() if f.is_file() and f.name in LICENCE_NAMES) if folder.is_dir() else []
        if not files:
            raise NoticeError(f"No licence or notice file for {package_id} {version} in {folder}")
        for file in files:
            notices.append(section(f"{package_id} {version} - {file.name}", file.read_text(encoding="utf-8", errors="replace")))

    covered = set(OWN)
    target = deps["targets"].get(deps["runtimeTarget"]["name"])
    if target is None:
        raise NoticeError(f"flevert.deps.json has no target named {deps['runtimeTarget']['name']}.")
    for library in target.values():
        for kind in ("runtime", "native", "resources"):
            for asset, details in library.get(kind, {}).items():
                name = Path(asset).name
                # dotnet publish puts assets in the folder itself, and satellite assemblies in a folder named after their culture.
                covered.add(f"{details['locale']}/{name}" if isinstance(details, dict) and "locale" in details else name)

    for entry in json.loads(manifest.read_text(encoding="utf-8")).get("notices", []):
        for pattern in entry["files"]:
            shipped = [str(Path(p).relative_to(publish)) for p in glob.glob(str(publish / pattern)) if Path(p).is_file()]
            # An entry left behind by an update would otherwise cover a renamed binary by its old name.
            if not shipped:
                raise NoticeError(f"{manifest} lists {pattern} under \"{entry['title']}\", but the publish folder has no such file: "
                                  "make the notices match what the build ships.")
            covered.update(shipped)
        preface = [f"Shipped as: {', '.join(entry['files'])}"] + ([entry["note"]] if "note" in entry else [])
        for pattern in entry["licences"]:
            matched = sorted(Path(p) for p in glob.glob(str(root / pattern)) if Path(p).is_file())
            if not matched:
                raise NoticeError(f"Nothing matches {pattern}, a licence of \"{entry['title']}\" listed in {manifest}. "
                                  "Run scripts/fetch-deps-linux.py, or fix the notices.")
            for licence in matched:
                notices.append(section(f"{entry['title']} - {licence.name}", licence.read_text(encoding="utf-8", errors="replace"), preface))
                preface = []

    shipped = sorted(str(p.relative_to(publish)) for p in publish.rglob("*") if p.is_file() and p.suffix != ".pdb")
    unlicensed = [name for name in shipped if name not in covered]
    if unlicensed:
        raise NoticeError(f"No licence notice for {', '.join(unlicensed)} in {publish}. The package would ship them without their "
                          "licences: add them to \"notices\" in scripts/deps-linux.json with their licence files, or keep them out of the build.")
    (publish / "THIRD-PARTY-NOTICES.txt").write_text("".join(notices), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--publish", type=Path, required=True)
    parser.add_argument("--packages", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, default=ROOT / "scripts" / "deps-linux.json")
    parser.add_argument("--root", type=Path, default=ROOT)
    args = parser.parse_args()
    try:
        write(args.publish.resolve(), args.packages, args.manifest, args.root)
    except (NoticeError, OSError, KeyError, ValueError) as e:
        print(e, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
