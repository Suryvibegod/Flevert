#!/bin/sh
# Validate ownership and record installed files before changing an existing app.
set -eu
here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
exec /usr/bin/python3 -I "$here/install.py" "$@"
