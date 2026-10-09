#!/bin/sh
# Builds the macOS release from a published (not single-file) Tempo folder. Runs on macOS.
#   packaging/macos/build-app.sh <version> <arch: arm64 | x64> <published folder> <output folder>
# Produces Tempo.app in the output folder and Tempo-<version>-macos-<arch>.zip beside it.
# The bundle is ad-hoc signed (Apple Silicon will not run unsigned code), not signed with a developer ID,
# so macOS asks the user to allow it the first time.
set -eu

version=$1
arch=$2
published=$3
out=$4

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

app="$out/Tempo.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$published"/. "$app/Contents/MacOS/"
chmod 755 "$app/Contents/MacOS/Tempo"
sed "s/@VERSION@/$version/g" "$root/packaging/macos/Info.plist.in" > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist"

# ---- Icon: every size iconutil expects, scaled from the program's logo ----
iconset="$work/Tempo.iconset"
mkdir "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$root/src/StanfordController.App/Assets/logo.png" --out "$iconset/icon_${size}x${size}.png" > /dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$root/src/StanfordController.App/Assets/logo.png" --out "$iconset/icon_${size}x${size}@2x.png" > /dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/Tempo.icns"

codesign --force --deep --sign - "$app"
codesign --verify --deep --strict --verbose=2 "$app"

zip="Tempo-$version-macos-$arch.zip"
rm -f "$out/$zip"
ditto -c -k --sequesterRsrc --keepParent "$app" "$out/$zip"
echo "Built $out/$zip"
