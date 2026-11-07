#!/usr/bin/env bash
# Builds the Noctis .deb (GitHub #191) from a self-contained linux publish folder.
#
#   packaging/linux/build-deb.sh <publish-dir> <version> <amd64|arm64> <out.deb> [icon.png]
#
# Layout: the app in /opt/noctis, a `noctis` launcher in /usr/bin, a desktop entry and
# hicolor icons, so it shows up in app search and pins to the dock like any installed app.
# Unlike the AppImage it bundles no libvlc: it uses the distro's (libvlc5 +
# vlc-plugin-base), which the app already loads via libvlc.so.5 without the -dev package.
# icon.png: a square PNG (256 px or larger); without it the largest frame of
# src/Noctis/Assets/Icons/Noctis.ico is used (needs ImageMagick either way).
set -euo pipefail

if [ $# -lt 4 ]; then
  echo "usage: $0 <publish-dir> <version> <amd64|arm64> <out.deb> [icon.png]" >&2
  exit 2
fi

PAYLOAD=$1
VERSION=$2
ARCH=$3
OUT=$4
ICON=${5:-}
REPO_ROOT=$(cd "$(dirname "$0")/../.." && pwd)

test -x "$PAYLOAD/Noctis" || { echo "no executable Noctis in $PAYLOAD" >&2; exit 1; }
case "$ARCH" in amd64|arm64) ;; *) echo "unsupported arch: $ARCH" >&2; exit 2 ;; esac

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
APPDIR="$STAGE/opt/noctis"
mkdir -p "$APPDIR" "$STAGE/usr/bin" "$STAGE/usr/share/applications" "$STAGE/DEBIAN"

cp -a "$PAYLOAD/." "$APPDIR/"
chmod 755 "$APPDIR/Noctis"
if [ -f "$APPDIR/ffmpeg" ]; then chmod 755 "$APPDIR/ffmpeg"; fi
# Tells the in-app updater dpkg owns these files (UpdateService.DebPackageMarkerFileName):
# it points to the next .deb instead of extracting a tarball over them.
echo "deb" > "$APPDIR/.noctis-deb-package"

# A script, not a symlink, so the app always starts from its own folder.
cat > "$STAGE/usr/bin/noctis" <<'LAUNCHER'
#!/bin/sh
exec /opt/noctis/Noctis "$@"
LAUNCHER
chmod 755 "$STAGE/usr/bin/noctis"

# Same entry as the AppImage's, plus StartupWMClass: the window's WM_CLASS is "Noctis",
# which is how GNOME/Zorin tie the open window to this launcher (pinning, dock icon).
cat > "$STAGE/usr/share/applications/noctis.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=Noctis
Comment=A music player that respects what's yours
Exec=noctis %F
Icon=noctis
Categories=AudioVideo;Audio;Player;
Terminal=false
StartupWMClass=Noctis
DESKTOP

# Icons. Ubuntu ships ImageMagick 6 (`convert`/`identify`), newer distros 7 (`magick`).
if command -v magick >/dev/null 2>&1; then IM_CONVERT="magick"; IM_IDENTIFY="magick identify"
elif command -v convert >/dev/null 2>&1; then IM_CONVERT="convert"; IM_IDENTIFY="identify"
else echo "ImageMagick is required for the icons" >&2; exit 1; fi
if [ -z "$ICON" ]; then
  SRC_ICO="$REPO_ROOT/src/Noctis/Assets/Icons/Noctis.ico"
  # Largest frame, so every size is a downscale.
  LARGEST_IDX=$($IM_IDENTIFY -format '%p %w\n' "$SRC_ICO" | sort -k2 -n | tail -1 | cut -d' ' -f1)
  ICON="$STAGE/noctis-src.png"
  $IM_CONVERT "${SRC_ICO}[${LARGEST_IDX}]" -background none "$ICON"
fi
for size in 16 24 32 48 64 128 256; do
  dir="$STAGE/usr/share/icons/hicolor/${size}x${size}/apps"
  mkdir -p "$dir"
  $IM_CONVERT "$ICON" -background none -resize "${size}x${size}" "$dir/noctis.png"
done
rm -f "$STAGE/noctis-src.png"

# Runtime dependencies. Native libs the payload loads that it does not carry:
# glibc 2.34+ / libstdc++ / zlib / OpenSSL 3 (the .NET runtime; ICU comes in through
# libvlc's libxml2), fontconfig + X11 (Avalonia, Skia), GL, and libvlc with the plugin set
# the AppImage bundles from the same Ubuntu package (filesystem, avformat, pulse, imem).
# libssl3t64 is Ubuntu 24.04's name (it Provides libssl3; the alternative is belt-and-braces).
DEPENDS="libc6 (>= 2.34), libgcc-s1, libstdc++6, zlib1g, libssl3 | libssl3t64, libfontconfig1, libx11-6, libice6, libsm6, libxcursor1, libxext6, libxfixes3, libxi6, libxrandr2, libgl1, libvlc5, vlc-plugin-base"
INSTALLED_KB=$(du -sk --exclude=DEBIAN "$STAGE" | cut -f1)

cat > "$STAGE/DEBIAN/control" <<CONTROL
Package: noctis
Version: ${VERSION}
Architecture: ${ARCH}
Maintainer: heartached <172862528+heartached@users.noreply.github.com>
Installed-Size: ${INSTALLED_KB}
Depends: ${DEPENDS}
Recommends: zenity
Section: sound
Priority: optional
Homepage: https://github.com/heartached/Noctis
Description: A music player that respects what's yours
 Noctis is a desktop music player for your local library: fast browsing,
 synced lyrics, metadata editing, EQ, and more.
CONTROL

# Refresh the desktop/icon caches when the tools exist (they are not dependencies).
cat > "$STAGE/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e
if [ "$1" = "configure" ]; then
  command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
  command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi
exit 0
POSTINST
cp "$STAGE/DEBIAN/postinst" "$STAGE/DEBIAN/postrm"
sed -i 's/if \[ "\$1" = "configure" \]; then/if [ "$1" = "remove" ] || [ "$1" = "purge" ]; then/' "$STAGE/DEBIAN/postrm"
chmod 755 "$STAGE/DEBIAN/postinst" "$STAGE/DEBIAN/postrm"

# dpkg wants directories 755 and files without group/other write.
find "$STAGE" -type d -exec chmod 755 {} +
chmod -R go-w "$STAGE"

mkdir -p "$(dirname "$OUT")"
dpkg-deb --build --root-owner-group -Zxz "$STAGE" "$OUT"
echo "built $OUT"
