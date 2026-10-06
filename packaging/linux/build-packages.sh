#!/bin/sh
# Builds the Linux release packages from a published single-file Tempo binary.
#   packaging/linux/build-packages.sh <version> <path to published Tempo binary> <output folder>
# Produces Tempo-<version>-linux-x64.tar.gz, tempo-dg645_<version>_amd64.deb and a PKGBUILD for Arch Linux.
# The package and command are named tempo-dg645 because other software (the TEMPO pulsar-timing package,
# Grafana Tempo) already installs a command called tempo.
set -eu

version=$1
binary=$2
out=$3
repository=${REPOSITORY_URL:-https://github.com/samwells2000/Standford-Controller}

root=$(cd "$(dirname "$0")/../.." && pwd)
icon="$root/src/StanfordController.App/Assets/logo.png"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

# ---- Tarball: the program, its desktop entry and icon, and an install script ----
mkdir -p "$work/tar/tempo-dg645"
install -m755 "$binary" "$work/tar/tempo-dg645/tempo-dg645"
install -m644 "$root/packaging/linux/tempo-dg645.desktop" "$work/tar/tempo-dg645/tempo-dg645.desktop"
install -m644 "$icon" "$work/tar/tempo-dg645/tempo-dg645.png"
install -m755 "$root/packaging/linux/install.sh" "$work/tar/tempo-dg645/install.sh"
cat > "$work/tar/tempo-dg645/README.txt" <<TEXT
Tempo $version for Linux (x86-64)

Run it straight from this folder:   ./tempo-dg645
Install it for yourself:            ./install.sh
Install it for everyone:            sudo ./install.sh --system

Needs an X11 or XWayland desktop (libX11, libICE, libSM and fontconfig, present on
any desktop install). Configurations are kept in ~/.config/Tempo.
User guide: $repository/blob/main/docs/USER-GUIDE.md
TEXT
tarball="Tempo-$version-linux-x64.tar.gz"
tar -C "$work/tar" --owner=0 --group=0 -czf "$out/$tarball" tempo-dg645

# ---- Debian / Ubuntu package ----
deb="$work/deb"
install -Dm755 "$binary" "$deb/opt/tempo-dg645/tempo-dg645"
mkdir -p "$deb/usr/bin"
ln -s /opt/tempo-dg645/tempo-dg645 "$deb/usr/bin/tempo-dg645"
install -Dm644 "$root/packaging/linux/tempo-dg645.desktop" "$deb/usr/share/applications/tempo-dg645.desktop"
install -Dm644 "$icon" "$deb/usr/share/icons/hicolor/128x128/apps/tempo-dg645.png"
mkdir -p "$deb/DEBIAN"
size=$(du -sk "$deb" | cut -f1)
cat > "$deb/DEBIAN/control" <<CONTROL
Package: tempo-dg645
Version: $version
Section: science
Priority: optional
Architecture: amd64
Installed-Size: $size
Depends: libx11-6, libice6, libsm6, libfontconfig1
Maintainer: Tempo project <tempo-dg645@users.noreply.github.com>
Homepage: $repository
Description: Control software for Stanford Research Systems DG645 delay generators
 Tempo controls SRS DG645 digital delay generators over Ethernet: channel
 delays, output levels, trigger, burst, status and interfaces. Instruments
 and their complete setups are saved in configuration files so they can be
 restored whenever an instrument is switched on.
CONTROL
dpkg-deb --root-owner-group --build "$deb" "$out/tempo-dg645_${version}_amd64.deb" >/dev/null

# ---- Arch Linux PKGBUILD, pointing at the tarball in the GitHub release ----
sha=$(sha256sum "$out/$tarball" | cut -d' ' -f1)
sed -e "s|@VERSION@|$version|" -e "s|@REPOSITORY@|$repository|" -e "s|@SHA256@|$sha|" \
    "$root/packaging/arch/PKGBUILD.in" > "$out/PKGBUILD"

ls -la "$out"
