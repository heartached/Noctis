#!/usr/bin/env python3
"""Generates every Android launcher icon and the Play Store graphics from the Noctis logo.

Source: src/Noctis.UI/Assets/Icons/Noctis Logo Clean.png (2000x2000 RGBA: a #E74856
circle filling the canvas with a white beamed-note glyph; byte-identical to
src/Noctis.Android/Icon.png). There is no vector master in the repo, so the 2000 px
PNG is the highest-resolution source; every output is a downscale of it.

Outputs (paths relative to the repo root, or to --out-root when given):
  src/Noctis.Android/Resources/mipmap-<dpi>/ic_launcher.png             legacy icon (API < 26)
  src/Noctis.Android/Resources/mipmap-<dpi>/ic_launcher_round.png       legacy round icon
  src/Noctis.Android/Resources/mipmap-<dpi>/ic_launcher_foreground.png  adaptive foreground
                                                                        (also the monochrome layer)
  src/Noctis.Android/Resources/mipmap-anydpi-v26/ic_launcher.xml        adaptive icon
  src/Noctis.Android/Resources/mipmap-anydpi-v26/ic_launcher_round.xml  adaptive round icon
  src/Noctis.Android/Resources/values/ic_launcher_background.xml        #E74856
  store/android/icon-512.png                                            Play hi-res icon
  store/android/feature-graphic.png                                     Play feature graphic 1024x500

Adaptive geometry: the layer canvas is 108 dp; launchers mask the centre 72 dp and
guarantee the centre 66 dp circle (the safe zone). The logo circle is mapped onto the
72 dp mask, so the note keeps the logo's own proportions and offset; its farthest
pixel lands 25.8 dp from the centre, inside the 33 dp safe radius (checked below).
"""
import argparse
import math
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SOURCE = os.path.join(REPO, "src", "Noctis.UI", "Assets", "Icons", "Noctis Logo Clean.png")
FONT = os.path.join(REPO, "src", "Noctis.UI", "Assets", "Fonts", "Inter-SemiBold.ttf")
BRAND_RED = (0xE7, 0x48, 0x56)   # the logo's circle colour, sampled: (231, 72, 86)
INK = (0x15, 0x15, 0x15)          # splash_background / Gray theme window background
DENSITIES = {"mdpi": 1.0, "hdpi": 1.5, "xhdpi": 2.0, "xxhdpi": 3.0, "xxxhdpi": 4.0}
LAYER_DP, MASK_DP, SAFE_DP = 108, 72, 66
LEGACY_DP, LEGACY_CIRCLE_DP = 48, 44   # Material legacy launcher keyline for circular icons


def note_mask(logo):
    """Alpha mask of the white note alone. The glyph is white over #E74856, so the green
    channel runs 72 (red) -> 255 (white) across its anti-aliased edge; mapping that range to
    0..255 recovers the edge coverage exactly, times the logo alpha for the outer rim."""
    r, g, b, a = logo.split()
    lo, hi = BRAND_RED[1], 255
    cover = g.point(lambda v: 0 if v <= lo else 255 if v >= hi else round((v - lo) * 255 / (hi - lo)))
    return ImageChops.multiply(cover, a)


def white_glyph(mask):
    img = Image.new("RGBA", mask.size, (255, 255, 255, 0))
    img.putalpha(mask)
    return img


def scaled(img, size):
    return img.resize((size, size), Image.LANCZOS)


def adaptive_foreground(mask, px):
    """Note on a transparent px-square 108 dp canvas, logo circle mapped onto the 72 dp mask."""
    circle_px = round(px * MASK_DP / LAYER_DP)
    glyph = scaled(white_glyph(mask), circle_px)
    canvas = Image.new("RGBA", (px, px), (255, 255, 255, 0))
    off = (px - circle_px) // 2
    canvas.alpha_composite(glyph, (off, off))
    return canvas


def legacy_icon(logo, px):
    circle_px = round(px * LEGACY_CIRCLE_DP / LEGACY_DP)
    canvas = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    off = (px - circle_px) // 2
    canvas.alpha_composite(scaled(logo, circle_px), (off, off))
    return canvas


def check_safe_zone(mask):
    """Fails the run if any note pixel would fall outside the 66 dp safe circle."""
    w = mask.width
    bbox = mask.getbbox()
    px = mask.load()
    far = 0.0
    for y in range(bbox[1], bbox[3]):
        for x in range(bbox[0], bbox[2], 2):
            if px[x, y] > 8:
                far = max(far, math.hypot(x + 0.5 - w / 2, y + 0.5 - w / 2))
    far_dp = far / (w / 2) * (MASK_DP / 2)
    print(f"note reaches {far_dp:.1f} dp from centre (safe radius {SAFE_DP / 2:.0f} dp)")
    if far_dp > SAFE_DP / 2:
        sys.exit("note leaves the adaptive-icon safe zone")


def play_icon(mask):
    """512x512 opaque square: brand red full bleed (Play applies its own corner mask), the
    note at the logo's own scale and offset."""
    icon = Image.new("RGBA", (512, 512), BRAND_RED + (255,))
    icon.alpha_composite(scaled(white_glyph(mask), 512))
    return icon


def feature_graphic(logo):
    """1024x500, no alpha: ink background, logo left, wordmark + line right. Everything sits
    inside a 64 px margin so Play's crops and the play-button overlay never cut it."""
    fg = Image.new("RGB", (1024, 500), INK)
    draw = ImageDraw.Draw(fg)
    # soft accent glow behind the logo
    glow = Image.new("L", (1024, 500), 0)
    ImageDraw.Draw(glow).ellipse((40, 30, 480, 470), fill=70)
    for radius in (60, 40, 20):
        glow = glow.filter(ImageFilter.GaussianBlur(radius))
    fg.paste(Image.new("RGB", (1024, 500), BRAND_RED), (0, 0), glow)
    logo_px = 300
    fg.paste(scaled(logo, logo_px), (110, 100), scaled(logo, logo_px))
    title = ImageFont.truetype(FONT, 112)
    line = ImageFont.truetype(FONT, 34)
    draw.text((470, 148), "Noctis", font=title, fill=(255, 255, 255))
    draw.text((474, 290), "Word-synced lyrics for", font=line, fill=(200, 200, 200))
    draw.text((474, 336), "your own music library", font=line, fill=(200, 200, 200))
    return fg


def write(img, root, *parts):
    path = os.path.join(root, *parts)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path, optimize=True)
    print("wrote", os.path.relpath(path, root))


def write_text(text, root, *parts):
    path = os.path.join(root, *parts)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    print("wrote", os.path.relpath(path, root))


ADAPTIVE_XML = """<?xml version="1.0" encoding="utf-8"?>
<!-- Generated by scripts/android/make-icons.py; edit the script, not this file. -->
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@color/ic_launcher_background" />
    <foreground android:drawable="@mipmap/ic_launcher_foreground" />
    <!-- Android 13+ themed icons tint this layer's alpha; the white note alone is exactly that. -->
    <monochrome android:drawable="@mipmap/ic_launcher_foreground" />
</adaptive-icon>
"""

BACKGROUND_XML = """<?xml version="1.0" encoding="utf-8"?>
<!-- Generated by scripts/android/make-icons.py: the logo circle's red, sampled from the source PNG. -->
<resources>
  <color name="ic_launcher_background">#E74856</color>
</resources>
"""


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out-root", default=REPO, help="write outputs under this root instead of the repo")
    root = ap.parse_args().out_root

    logo = Image.open(SOURCE).convert("RGBA")
    if logo.size != (2000, 2000):
        sys.exit(f"unexpected source size {logo.size}; update the geometry notes before regenerating")
    mask = note_mask(logo)
    check_safe_zone(mask)

    res = ("src", "Noctis.Android", "Resources")
    for dpi, scale in DENSITIES.items():
        legacy = legacy_icon(logo, round(LEGACY_DP * scale))
        write(legacy, root, *res, f"mipmap-{dpi}", "ic_launcher.png")
        write(legacy, root, *res, f"mipmap-{dpi}", "ic_launcher_round.png")
        write(adaptive_foreground(mask, round(LAYER_DP * scale)), root, *res, f"mipmap-{dpi}", "ic_launcher_foreground.png")
    write_text(ADAPTIVE_XML, root, *res, "mipmap-anydpi-v26", "ic_launcher.xml")
    write_text(ADAPTIVE_XML, root, *res, "mipmap-anydpi-v26", "ic_launcher_round.xml")
    write_text(BACKGROUND_XML, root, *res, "values", "ic_launcher_background.xml")
    write(play_icon(mask), root, "store", "android", "icon-512.png")
    write(feature_graphic(logo), root, "store", "android", "feature-graphic.png")


if __name__ == "__main__":
    main()
