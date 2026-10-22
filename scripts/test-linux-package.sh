#!/bin/bash
# Exercise the real packaged installer and CLI without touching the runner's own profile or Nautilus menu.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props")
temporary=$(mktemp -d)
trap 'rm -rf -- "$temporary"' EXIT
umask 077
mkdir -- "$temporary/home" "$temporary/inputs"
tar -xzf "$root/artifacts/linux/Flevert-$version-linux-x64.tar.gz" -C "$temporary"
export HOME="$temporary/home" XDG_DATA_HOME="$temporary/home/data" FLEVERT_HEADLESS=1
package="$temporary/flevert-$version"
app="$XDG_DATA_HOME/Flevert/app"
"$package/install.sh"
test -f "$XDG_DATA_HOME/Flevert/.flevert-install.json"
test -f "$XDG_DATA_HOME/nautilus-python/extensions/flevert_menu.py"
cp -- "$root/tests/Flevert.Image.Tests/fixtures/gradient-320x240.heic" "$temporary/inputs/photo.heic"
cp -- "$root/tests/Flevert.Pdf.Tests/fixtures/rotations.pdf" "$temporary/inputs/pages.pdf"
"$app/flevert" run image.to-png "$temporary/inputs/photo.heic"
"$app/flevert" run pdf.to-jpg "$temporary/inputs/pages.pdf"
test -f "$temporary/inputs/photo.png"
test -d "$temporary/inputs/pages (pages)"
echo 'keep this user file' > "$XDG_DATA_HOME/Flevert/personal-note.txt"
"$package/install.sh"
"$app/install.sh" --uninstall
test ! -e "$app"
test ! -e "$XDG_DATA_HOME/nautilus-python/extensions/flevert_menu.py"
test ! -e "$XDG_DATA_HOME/Flevert/.flevert-install.json"
test "$(cat "$XDG_DATA_HOME/Flevert/personal-note.txt")" = 'keep this user file'
echo 'The real Linux package installed, updated, converted an image/PDF, uninstalled and preserved the user file.'
