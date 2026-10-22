#!/bin/bash
# Optional manual-CI evidence: run the new regression tests against the old menu/installer, then restore the fixed tree.
set -euo pipefail
baseline=${1:-}
[[ "$baseline" =~ ^[0-9a-f]{40}$ ]] || { echo 'A full commit SHA is required.' >&2; exit 2; }
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"
backup=$(mktemp -d)
menu=src/Flevert.Core/Unix/UnixMenu.cs
installer=packaging/linux/install.sh
helper=packaging/linux/install.py
cp -- "$menu" "$backup/menu.cs"
cp -- "$installer" "$backup/install.sh"
cp -- "$helper" "$backup/install.py"
restore() {
    cp -- "$backup/menu.cs" "$menu"
    cp -- "$backup/install.sh" "$installer"
    cp -- "$backup/install.py" "$helper"
    rm -rf -- "$backup"
}
trap restore EXIT
git fetch --no-tags --depth=1 origin "$baseline"
git show "$baseline:$menu" > "$menu"
git show "$baseline:$installer" > "$installer"
rm -- "$helper"
if dotnet test tests/Flevert.Core.Tests -c Release --filter 'FullyQualifiedName~UnixMenuInstallerTests' \
    --results-directory "$backup/results" --logger 'trx;LogFileName=linux-menu-red.trx'; then
    echo 'The menu regression tests did not detect the old unsafe code.' >&2
    exit 1
fi
# A compile failure is not a RED test: require actual executed test failures.
python3 -I -c 'import sys,xml.etree.ElementTree as E; r=E.parse(sys.argv[1]).getroot(); n={"t":"http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}; c=r.find("t:ResultSummary/t:Counters",n); assert int(c.attrib["failed"])>0' \
    "$backup/results/linux-menu-red.trx"
if python3 -I -m unittest discover -s packaging/linux -p 'test_install.py'; then
    echo 'The installer regression tests did not detect the old destructive code.' >&2
    exit 1
fi
echo 'RED confirmed for the old Linux menu and installer. Restoring the fixed sources for the normal build and tests.'
