#!/usr/bin/env bash
# Installs PairSync for the current user only (no root): program to ~/.local/opt/pairsync,
# menu entry and icon below ~/.local/share. Run again to update; ./install.sh --uninstall removes it.
# Your data (~/.local/share/pairsync) is never touched.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
prefix="${XDG_DATA_HOME:-$HOME/.local/share}"
target="$HOME/.local/opt/pairsync"
desktop="$prefix/applications/pairsync.desktop"
icon="$prefix/icons/hicolor/scalable/apps/pairsync.svg"

if [[ "${1:-}" == "--uninstall" ]]; then
    rm -rf "$target" "$desktop" "$icon"
    echo "PairSync removed. Your data in $prefix/pairsync was kept."
    exit 0
fi

mkdir -p "$target" "$(dirname "$desktop")" "$(dirname "$icon")"
cp -a "$here/app/." "$target/"
chmod +x "$target/PairSync"
cp "$here/pairsync.svg" "$icon"
sed "s|@EXEC@|\"$target/PairSync\"|" "$here/pairsync.desktop" > "$desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "$prefix/applications" || true
echo "PairSync installed to $target. Start it from the application menu."
