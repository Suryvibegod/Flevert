#!/bin/sh
# Flevert for Linux: installs it for this user, without root, and puts it in the Nautilus right-click menu.
#
#   ./install.sh               install, or update an earlier install
#   ./install.sh --uninstall   take Flevert out of the menu and remove it, with its logs
#
# The app goes to ~/.local/share/Flevert/app (under XDG_DATA_HOME when set), a folder only this user can open.
set -eu

case "${1:-}" in
    "" | --uninstall) ;;
    *) echo "Usage: $0 [--uninstall]" >&2; exit 2 ;;
esac

here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
flevert_home=$data/Flevert
app=$flevert_home/app

if [ "${1:-}" = "--uninstall" ]; then
    if [ -x "$app/flevert" ]; then
        "$app/flevert" uninstall-menu || true
    fi
    rm -rf -- "$flevert_home"
    echo "Flevert is removed. Restart Nautilus to see the menu go: nautilus -q"
    exit 0
fi

# What Flevert runs on the way (util-linux, GLib, zenity, fontconfig): present on GNOME desktops.
missing=""
for tool in setsid setpriv prlimit gdbus zenity fc-match; do
    if [ ! -x "/usr/bin/$tool" ] && [ ! -x "/bin/$tool" ]; then
        missing="$missing $tool"
    fi
done
if [ -n "$missing" ]; then
    echo "Flevert needs these programs, which are missing:$missing" >&2
    echo "On Ubuntu, Debian or Pop!_OS: sudo apt install util-linux libglib2.0-bin zenity fontconfig" >&2
    exit 1
fi

# A folder only this user can open, and an app nobody else can change: the menu runs it as this user.
umask 077
mkdir -p -- "$flevert_home"
chmod 700 -- "$flevert_home"
staging=$flevert_home/app.new.$$
previous=$flevert_home/app.old.$$
rm -rf -- "$staging"
cp -R -- "$here" "$staging"
chmod -R u+rwX,go-w -- "$staging"
if [ -d "$app" ]; then
    mv -- "$app" "$previous"
fi
mv -- "$staging" "$app"
rm -rf -- "$previous"

"$app/flevert" install-menu
