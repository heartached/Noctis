#!/usr/bin/env python3
"""Generates the icons of Noctis's official plugins (icon.png next to each plugin.json).

Same approach as scripts/app-icons/generate_app_icons.py: every shape is an analytic signed
distance field evaluated on a supersampled canvas (SS x the output edge), turned into coverage
and box-filtered down to the exact pixel size, so edges are clean at 256 px and below.
Colours are flat (a whisper of vertical tone on the tile only).

Icons:
  album-roulette  coral #FF6B5A tile, a white vinyl disc cut into 6 wheel wedges, a small
                  pointer notch on top.
  ab-loop         teal #2EC4B6 tile, a white open loop arrow arching from the "B" dot back to
                  the "A" dot over a thin flat waveform line.
  chords          violet #8B6CF6 tile, five white piano keys (C to G) with three violet black
                  keys, and a violet dot on C, E and G: a C major chord.

Outputs (paths relative to the repo root):
  plugins/Noctis.Plugins.AlbumRoulette/icon.png   256 px
  plugins/Noctis.Plugins.ABLoop/icon.png          256 px
  plugins/Noctis.Plugins.Chords/icon.png          256 px
  and, with --out DIR, a copy of each as DIR/<id>-icon.png

Requires Pillow and numpy.  Run:  python scripts/plugin-icons/generate_official_plugin_icons.py [--out DIR]
"""
from __future__ import annotations

import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))

SIZE = 256
SS = 4                      # supersampling factor (1024 px working canvas)
TILE_MARGIN = 0.035         # empty border around the tile, in units of the edge
TILE_RADIUS = 0.225         # corner radius of the tile, in units of the edge
WHITE = (1.0, 1.0, 1.0)


def hexrgb(h: str) -> np.ndarray:
    h = h.lstrip("#")
    return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], dtype=np.float64) / 255.0


def mix(a: np.ndarray, b: np.ndarray, t: float) -> np.ndarray:
    return a + (b - a) * t


# ── signed distance fields (unit coords: 0..1 across the canvas, y down) ──

def grid(n: int):
    c = (np.arange(n, dtype=np.float64) + 0.5) / n
    return np.meshgrid(c, c)  # x, y


def sd_circle(x, y, cx, cy, r):
    return np.hypot(x - cx, y - cy) - r


def sd_round_rect(x, y, x0, y0, x1, y1, r):
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    hx, hy = (x1 - x0) / 2 - r, (y1 - y0) / 2 - r
    qx, qy = np.abs(x - cx) - hx, np.abs(y - cy) - hy
    outside = np.hypot(np.maximum(qx, 0), np.maximum(qy, 0))
    inside = np.minimum(np.maximum(qx, qy), 0)
    return outside + inside - r


def sd_segment(x, y, ax, ay, bx, by, r):
    """Capsule: the segment a-b thickened to radius r (round caps)."""
    px, py = x - ax, y - ay
    vx, vy = bx - ax, by - ay
    h = np.clip((px * vx + py * vy) / (vx * vx + vy * vy), 0, 1)
    return np.hypot(px - vx * h, py - vy * h) - r


def sd_polygon(x, y, pts):
    """Exact SDF of a simple polygon (Inigo Quilez)."""
    d = (x - pts[0][0]) ** 2 + (y - pts[0][1]) ** 2
    s = np.ones_like(x)
    n = len(pts)
    j = n - 1
    for i in range(n):
        ex, ey = pts[j][0] - pts[i][0], pts[j][1] - pts[i][1]
        wx, wy = x - pts[i][0], y - pts[i][1]
        h = np.clip((wx * ex + wy * ey) / (ex * ex + ey * ey), 0, 1)
        bx, by = wx - ex * h, wy - ey * h
        d = np.minimum(d, bx * bx + by * by)
        c1 = y >= pts[i][1]
        c2 = y < pts[j][1]
        c3 = ex * wy > ey * wx
        flip = (c1 & c2 & c3) | (~c1 & ~c2 & ~c3)
        s = np.where(flip, -s, s)
        j = i
    return s * np.sqrt(d)


def sd_arc(x, y, cx, cy, r, a0, a1, w):
    """A ring segment of radius r and half-width w between angles a0..a1 (radians, math
    orientation: 0 = right, pi/2 = up), with round caps."""
    ang = np.arctan2(-(y - cy), x - cx)            # y down -> flip for math angles
    ang = np.where(ang < a0, ang + 2 * math.pi, ang)
    on = (ang >= a0) & (ang <= a1)
    ring = np.abs(np.hypot(x - cx, y - cy) - r) - w
    e0 = (cx + r * math.cos(a0), cy - r * math.sin(a0))
    e1 = (cx + r * math.cos(a1), cy - r * math.sin(a1))
    caps = np.minimum(np.hypot(x - e0[0], y - e0[1]), np.hypot(x - e1[0], y - e1[1])) - w
    return np.where(on, ring, caps)


def coverage(sd, px):
    """Signed distance (unit coords) -> 0..1 coverage, 1 working pixel of antialiasing."""
    return np.clip(0.5 - sd / px, 0, 1)


def text_mask(n: int, text: str, cx: float, cy: float, height: float) -> np.ndarray:
    """Coverage of bold text centred at (cx, cy), cap height ~height (unit coords)."""
    font = None
    for name in ("arialbd.ttf", "Arial Bold.ttf", "DejaVuSans-Bold.ttf", "LiberationSans-Bold.ttf"):
        for base in ("", "C:/Windows/Fonts", "/usr/share/fonts/truetype/dejavu", "/Library/Fonts"):
            try:
                font = ImageFont.truetype(os.path.join(base, name) if base else name, int(height * n * 1.38))
                break
            except OSError:
                continue
        if font:
            break
    if font is None:
        font = ImageFont.load_default()
    im = Image.new("L", (n, n), 0)
    draw = ImageDraw.Draw(im)
    l, t, r, b = draw.textbbox((0, 0), text, font=font)
    draw.text((cx * n - (l + r) / 2, cy * n - (t + b) / 2), text, font=font, fill=255)
    return np.asarray(im, dtype=np.float64) / 255.0


# ── composition ──

def tile(x, y, px, base: str):
    """The flat rounded square, with a barely-there top-to-bottom tone."""
    m = TILE_MARGIN
    a = coverage(sd_round_rect(x, y, m, m, 1 - m, 1 - m, TILE_RADIUS * (1 - 2 * m)), px)
    c = hexrgb(base)
    top, bottom = mix(c, np.ones(3), 0.06), mix(c, np.zeros(3), 0.05)
    t = np.clip((y - m) / (1 - 2 * m), 0, 1)[..., None]
    rgb = top + (bottom - top) * t
    return rgb, a


def roulette(n: int):
    x, y = grid(n)
    px = 1.0 / n
    rgb, alpha = tile(x, y, px, "#FF6B5A")
    cx, cy, R = 0.5, 0.545, 0.300
    gap = 0.016          # half-width of the cuts between the wedges
    dist = np.hypot(x - cx, y - cy)
    ang = np.arctan2(-(y - cy), x - cx)

    # Six wedges, one centred under the pointer (straight up): cuts at 90 +- 30 + k*60 degrees.
    disc = sd_circle(x, y, cx, cy, R)
    cut = np.full_like(x, 1e9)
    for k in range(6):
        a = math.radians(60 + 60 * k)
        ex, ey = cx + math.cos(a) * R * 1.2, cy - math.sin(a) * R * 1.2
        cut = np.minimum(cut, sd_segment(x, y, cx, cy, ex, ey, gap))
    hub = sd_circle(x, y, cx, cy, 0.092)
    shape = np.maximum(disc, -np.minimum(cut, hub))          # disc minus cuts minus the hub

    # Vinyl grooves: two faint coral rings across the wedges.
    grooves = np.minimum(np.abs(dist - 0.205) - 0.006, np.abs(dist - 0.250) - 0.005)
    groove_a = coverage(grooves, px) * 0.22

    # The label hole and spindle: a white dot in the hub.
    spindle = sd_circle(x, y, cx, cy, 0.030)

    # Pointer notch: a small rounded triangle above the disc pointing down at the top wedge,
    # cut free of the disc by a coral moat.
    top = cy - R
    tri = [(cx - 0.062, top - 0.085), (cx + 0.062, top - 0.085), (cx, top + 0.030)]
    pointer = sd_polygon(x, y, tri) - 0.010
    shape = np.maximum(shape, -(pointer - 0.020))            # moat around the pointer

    white = coverage(np.minimum(np.minimum(shape, spindle), pointer), px)
    white = np.clip(white - groove_a * coverage(shape, px), 0, 1)
    rgb = rgb * (1 - white[..., None]) + np.array(WHITE) * white[..., None]
    return rgb, alpha


def ab_loop(n: int):
    x, y = grid(n)
    px = 1.0 / n
    rgb, alpha = tile(x, y, px, "#2EC4B6")
    line_y = 0.700
    ax_, bx_ = 0.305, 0.695
    dot_r = 0.072

    # A thin flat waveform: a hairline with short, quiet ticks, fading toward the ends.
    wave = sd_segment(x, y, 0.15, line_y, 0.85, line_y, 0.010)
    ticks = np.full_like(x, 1e9)
    heights = [0.020, 0.034, 0.026, 0.044, 0.030, 0.022, 0.038, 0.028, 0.046, 0.024, 0.032, 0.020]
    for i, h in enumerate(heights):
        tx = 0.18 + i * (0.64 / (len(heights) - 1))
        if abs(tx - ax_) < dot_r + 0.03 or abs(tx - bx_) < dot_r + 0.03:
            continue
        ticks = np.minimum(ticks, sd_segment(x, y, tx, line_y - h, tx, line_y + h, 0.0085))
    wave_a = coverage(np.minimum(wave, ticks), px) * 0.62

    # The loop arrow: an open arc from just above B, over the top, coming down at A.
    acx, acy, ar, aw = 0.5, 0.560, 0.195, 0.030
    arc = sd_arc(x, y, acx, acy, ar, math.radians(0), math.radians(168), aw)
    end_a = math.radians(168)
    ex, ey = acx + ar * math.cos(end_a), acy - ar * math.sin(end_a)
    head_tip_y = line_y - dot_r - 0.022
    head = sd_polygon(x, y, [(ex - 0.070, ey - 0.012), (ex + 0.066, ey - 0.012), (ax_ - 0.002, head_tip_y)]) - 0.008
    arrow = np.minimum(arc, head)

    dots = np.minimum(sd_circle(x, y, ax_, line_y, dot_r), sd_circle(x, y, bx_, line_y, dot_r))
    dots_a = coverage(dots, px)
    letters = np.maximum(text_mask(n, "A", ax_, line_y + 0.002, 0.070), text_mask(n, "B", bx_, line_y + 0.002, 0.070))

    white = np.maximum(np.maximum(wave_a * (1 - dots_a), coverage(arrow, px)), dots_a * (1 - letters))
    rgb = rgb * (1 - white[..., None]) + np.array(WHITE) * white[..., None]
    return rgb, alpha


def chords(n: int):
    x, y = grid(n)
    px = 1.0 / n
    base = "#8B6CF6"
    rgb, alpha = tile(x, y, px, base)
    x0, x1, y0, y1 = 0.175, 0.825, 0.255, 0.745
    keys = 5
    w = (x1 - x0) / keys
    gap = 0.011            # half-width of the violet line between two white keys
    # The keyboard: one white rounded block, cut into five keys.
    board = sd_round_rect(x, y, x0, y0, x1, y1, 0.045)
    cuts = np.full_like(x, 1e9)
    for k in range(1, keys):
        kx = x0 + k * w
        cuts = np.minimum(cuts, sd_segment(x, y, kx, y0 - 0.05, kx, y1 + 0.05, gap))
    white = np.maximum(board, -cuts)
    # Black keys between C-D, D-E and F-G (none between E and F), from the top edge down.
    blacks = np.full_like(x, 1e9)
    for k in (1, 2, 4):
        kx = x0 + k * w
        blacks = np.minimum(blacks, sd_round_rect(x, y, kx - 0.044, y0 - 0.05, kx + 0.044, y0 + 0.265, 0.018))
    white = np.maximum(white, -(blacks + 0.0))
    # The chord: C, E and G pressed (a violet dot near the bottom of each).
    dots = np.full_like(x, 1e9)
    for k in (0, 2, 4):
        dots = np.minimum(dots, sd_circle(x, y, x0 + (k + 0.5) * w, y1 - 0.075, 0.036))
    white = np.maximum(white, -dots)
    a = coverage(white, px)
    rgb = rgb * (1 - a[..., None]) + np.array(WHITE) * a[..., None]
    return rgb, alpha


ICONS = [
    ("album-roulette", roulette, "plugins/Noctis.Plugins.AlbumRoulette/icon.png"),
    ("ab-loop", ab_loop, "plugins/Noctis.Plugins.ABLoop/icon.png"),
    ("chords", chords, "plugins/Noctis.Plugins.Chords/icon.png"),
]


def render(fn, size: int) -> Image.Image:
    n = size * SS
    rgb, a = fn(n)
    # Box-filter down with colour premultiplied by coverage (no dark fringes on the edge).
    pre = np.concatenate([rgb * a[..., None], a[..., None]], axis=2)
    pre = pre.reshape(size, SS, size, SS, 4).mean(axis=(1, 3))
    alpha = pre[..., 3:4]
    color = np.where(alpha > 1e-6, pre[..., :3] / np.maximum(alpha, 1e-6), 0)
    out = np.concatenate([color, alpha], axis=2)
    return Image.fromarray(np.clip(np.round(out * 255), 0, 255).astype(np.uint8), "RGBA")


def main(argv: list[str]) -> int:
    out_dir = None
    if "--out" in argv:
        out_dir = argv[argv.index("--out") + 1]
        os.makedirs(out_dir, exist_ok=True)
    for name, fn, rel in ICONS:
        im = render(fn, SIZE)
        path = os.path.join(ROOT, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        im.save(path, optimize=True)
        print("wrote", rel)
        if out_dir:
            im.save(os.path.join(out_dir, f"{name}-icon.png"), optimize=True)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
