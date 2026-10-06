#!/bin/sh
# Installs Tempo from this folder.
#   ./install.sh              for the current user (~/.local), no administrator rights needed
#   sudo ./install.sh --system  for everyone (/usr/local)
#   ./install.sh --uninstall  (add --system to remove a system-wide install)
set -eu

here=$(cd "$(dirname "$0")" && pwd)
prefix="$HOME/.local"
uninstall=false
for arg in "$@"; do
    case "$arg" in
        --system) prefix=/usr/local ;;
        --uninstall) uninstall=true ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

if [ "$uninstall" = true ]; then
    rm -f "$prefix/bin/tempo-dg645" "$prefix/share/applications/tempo-dg645.desktop" "$prefix/share/icons/hicolor/128x128/apps/tempo-dg645.png"
    rm -rf "$prefix/lib/tempo-dg645"
    echo "Tempo removed from $prefix. Your configurations in ~/.config/Tempo were left in place."
    exit 0
fi

install -Dm755 "$here/tempo-dg645" "$prefix/lib/tempo-dg645/tempo-dg645"
mkdir -p "$prefix/bin"
ln -sf "$prefix/lib/tempo-dg645/tempo-dg645" "$prefix/bin/tempo-dg645"
install -Dm644 "$here/tempo-dg645.png" "$prefix/share/icons/hicolor/128x128/apps/tempo-dg645.png"
mkdir -p "$prefix/share/applications"
sed "s|^Exec=.*|Exec=$prefix/bin/tempo-dg645|" "$here/tempo-dg645.desktop" > "$prefix/share/applications/tempo-dg645.desktop"
chmod 644 "$prefix/share/applications/tempo-dg645.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$prefix/share/applications" >/dev/null 2>&1 || true

echo "Tempo installed in $prefix."
echo "Start it from your applications menu, or run: $prefix/bin/tempo-dg645"
