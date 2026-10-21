#!/bin/bash
# Builds the Linux package: artifacts/linux/Flevert-<version>-linux-x64.tar.gz and its .sha256 (docs/specs/linux.md).
# A self-contained flevert (.NET included), pdfium, qpdf, the Nautilus extension, install.sh, the licence and the
# third-party notices; scripts/write-notices-linux.py fails the build rather than ship a file without its licence.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
python=/usr/bin/python3
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props")
name=flevert-$version
out=$root/artifacts/linux
publish=$out/$name
tarball=$out/Flevert-$version-linux-x64.tar.gz

"$python" -I "$root/scripts/fetch-deps-linux.py"
# A stale file in the publish folder would be packed.
rm -rf "$publish" "$tarball" "$tarball.sha256"
dotnet publish "$root/src/Flevert.Linux" -c Release -r linux-x64 --self-contained -p:PublishReadyToRun=true -o "$publish"
find "$publish" -name '*.pdb' -delete
install -m 644 "$root/LICENSE" "$publish/LICENSE.txt"
install -m 755 "$root/packaging/linux/install.sh" "$publish/install.sh"
packages=$(dotnet nuget locals global-packages --list | sed 's/^global-packages: //')
"$python" -I "$root/scripts/write-notices-linux.py" --publish "$publish" --packages "$packages"

tar --sort=name --owner=0 --group=0 --numeric-owner -C "$out" -czf "$tarball" "$name"
(cd "$out" && sha256sum "$(basename "$tarball")" > "$(basename "$tarball").sha256")
echo "$tarball"
