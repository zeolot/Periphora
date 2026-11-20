#!/bin/sh
set -eu

HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
RULE_SOURCE="$HERE/99-periphora-hidraw.rules"
RULE_TARGET="/etc/udev/rules.d/99-periphora-hidraw.rules"

if [ ! -f "$HERE/Periphora" ]; then
    echo "Periphora executable not found next to this script." >&2
    exit 1
fi

chmod 755 "$HERE/Periphora"

if [ "$(id -u)" -eq 0 ]; then
    SUDO=""
else
    SUDO="sudo"
fi

echo "Installing hidraw access rule..."
$SUDO install -m 644 "$RULE_SOURCE" "$RULE_TARGET"
$SUDO udevadm control --reload-rules
$SUDO udevadm trigger --subsystem-match=hidraw

echo
echo "Done. Start Periphora with:"
echo "  $HERE/Periphora"
echo
echo "To check hardware detection:"
echo "  $HERE/Periphora --test"
echo
echo "If the receiver is still denied, unplug and reconnect it once."
