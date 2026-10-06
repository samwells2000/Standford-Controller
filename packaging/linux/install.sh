#!/bin/sh
# Installs Strobe from this folder.
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
    rm -f "$prefix/bin/strobe" "$prefix/share/applications/strobe.desktop" "$prefix/share/icons/hicolor/128x128/apps/strobe.png"
    rm -rf "$prefix/lib/strobe"
    echo "Strobe removed from $prefix. Your configurations in ~/.config/Strobe were left in place."
    exit 0
fi

install -Dm755 "$here/strobe" "$prefix/lib/strobe/strobe"
mkdir -p "$prefix/bin"
ln -sf "$prefix/lib/strobe/strobe" "$prefix/bin/strobe"
install -Dm644 "$here/strobe.png" "$prefix/share/icons/hicolor/128x128/apps/strobe.png"
mkdir -p "$prefix/share/applications"
sed "s|^Exec=.*|Exec=$prefix/bin/strobe|" "$here/strobe.desktop" > "$prefix/share/applications/strobe.desktop"
chmod 644 "$prefix/share/applications/strobe.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$prefix/share/applications" >/dev/null 2>&1 || true

echo "Strobe installed in $prefix."
echo "Start it from your applications menu, or run: $prefix/bin/strobe"
