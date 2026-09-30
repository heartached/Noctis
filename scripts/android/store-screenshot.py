#!/usr/bin/env python3
"""Captures the connected device's screen as a Google Play phone screenshot.

usage: store-screenshot.py <name> [--serial emulator-5554] [--out-dir store/android/screenshots]

Play's rules (Play Console Help, "Add preview assets"): JPEG or 24-bit PNG with no
alpha, each side 320-3840 px, and the long side at most twice the short side. adb's
PNG is RGBA and a 1080x2400 phone is 2.22:1, so this converts to RGB and refuses a
frame that breaks the ratio: run `adb shell wm size 1080x1920` first (9:16, Play's
recommended portrait size) and `adb shell wm size reset` afterwards.
"""
import argparse
import io
import os
import shutil
import subprocess
import sys

from PIL import Image

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))


def find_adb():
    local = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Android", "Sdk", "platform-tools", "adb.exe")
    return local if os.path.exists(local) else shutil.which("adb")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("name", help="file name without extension, e.g. 01-library")
    ap.add_argument("--serial", default="emulator-5554")
    ap.add_argument("--out-dir", default=os.path.join(REPO, "store", "android", "screenshots"))
    args = ap.parse_args()

    adb = find_adb()
    if not adb:
        sys.exit("adb not found")
    # exec-out, not shell: shell rewrites \n to \r\n on some builds and corrupts the PNG.
    png = subprocess.run([adb, "-s", args.serial, "exec-out", "screencap", "-p"], check=True, capture_output=True).stdout
    img = Image.open(io.BytesIO(png)).convert("RGB")
    w, h = img.size
    long_side, short_side = max(w, h), min(w, h)
    if short_side < 320 or long_side > 3840:
        sys.exit(f"{w}x{h}: each side must be 320-3840 px")
    if long_side > 2 * short_side:
        sys.exit(f"{w}x{h}: long side is more than twice the short side; run `adb shell wm size 1080x1920` first")
    os.makedirs(args.out_dir, exist_ok=True)
    path = os.path.join(args.out_dir, args.name + ".png")
    img.save(path, optimize=True)
    print(f"wrote {path} ({w}x{h}, RGB)")


if __name__ == "__main__":
    main()
