#!/usr/bin/env python3
"""Generates the "Add from YouTube" plugin icon (plugins/Noctis.Plugins.YouTubeImport/icon.png).

A flat rounded square in the official plugins' red/coral (a soft top-to-bottom gradient, the
same tile as the other official plugin icons), with one white glyph: a play triangle whose tip
runs down into a download arrow. Drawn the way scripts/app-icons/generate_app_icons.py draws
the app icons: on a large supersampled canvas, then box-filtered down to the exact size, so the
edges are clean at 256 px.

Requires Pillow and numpy.  Run:  python scripts/plugin-icons/generate_youtube_import_icon.py
"""
from __future__ import annotations

import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(ROOT, "plugins", "Noctis.Plugins.YouTubeImport", "icon.png")

SIZE = 256          # what plugin cards show (at up to 2x)
SS = 8              # supersampling factor
MARGIN = 8.5 / 256  # transparent edge around the tile, as the other plugin icons
RADIUS = 0.235      # corner radius, fraction of the tile edge
GLYPH_SCALE = 0.80  # glyph size on the tile (1 = as drawn on the 1024 grid)
TOP = (254, 115, 99)     # #FE7363
BOTTOM = (243, 102, 86)  # #F36656


def tile(size: int) -> Image.Image:
    """The rounded square with its vertical gradient, RGBA."""
    m = MARGIN * size
    edge = size - 2 * m
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((m, m, size - m, size - m), radius=RADIUS * edge, fill=255)
    t = np.linspace(0.0, 1.0, size)[:, None, None]
    grad = (np.array(TOP, dtype=np.float64) * (1 - t) + np.array(BOTTOM, dtype=np.float64) * t)
    rgb = np.broadcast_to(grad, (size, size, 3)).astype(np.uint8)
    img = Image.fromarray(rgb, "RGB").convert("RGBA")
    img.putalpha(mask)
    return img


def glyph(size: int) -> Image.Image:
    """White play triangle whose tip turns down into an arrow, as an L mask (0..255)."""
    s = size / 1024.0
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)

    def p(x: float, y: float) -> tuple[float, float]:
        return (x * s, y * s)

    # Play triangle, corners softened with a round-joined outline of the same shape.
    tri = [p(318, 236), p(318, 600), p(612, 418)]
    d.polygon(tri, fill=255)
    d.line(tri + [tri[0]], fill=255, width=int(64 * s), joint="curve")
    for x, y in tri:  # round caps at the joints
        r = 32 * s
        d.ellipse((x - r, y - r, x + r, y + r), fill=255)

    # The stem drops straight from the tip: its right edge is the tangent of the tip's rounded
    # point and its top lies inside the triangle, so play and arrow read as one shape...
    tip_x, tip_y, tip_r = 612, 418, 32
    stem_w = 88
    right = tip_x + tip_r
    d.rectangle((*p(right - stem_w, tip_y), *p(right, 700)), fill=255)
    # ...into the arrowhead.
    cx = right - stem_w / 2
    head = [p(cx - 150, 636), p(cx + 150, 636), p(cx, 792)]
    d.polygon(head, fill=255)
    d.line(head + [head[0]], fill=255, width=int(56 * s), joint="curve")
    for x, y in head:
        r = 28 * s
        d.ellipse((x - r, y - r, x + r, y + r), fill=255)
    return mask


def render(size: int) -> Image.Image:
    big = size * SS
    img = tile(big)
    white = Image.new("RGBA", (big, big), (255, 255, 255, 255))
    g = glyph(big)
    # Optical centre: the glyph's box is (286..778, 204..820); scale it about that centre and
    # put it in the middle of the tile.
    gx = (286 + 778) / 2 / 1024 * big
    gy = (204 + 820) / 2 / 1024 * big
    k = GLYPH_SCALE
    g = g.transform(g.size, Image.AFFINE, (1 / k, 0, gx - big / 2 / k, 0, 1 / k, gy - big / 2 / k), resample=Image.BILINEAR)
    img.paste(white, (0, 0), g)
    return img.resize((size, size), Image.BOX)


def main() -> int:
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    render(SIZE).save(OUT, optimize=True)
    print(OUT)
    return 0


if __name__ == "__main__":
    sys.exit(main())
