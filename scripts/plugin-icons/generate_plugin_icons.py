#!/usr/bin/env python3
"""Generates the plugin icons shown in Settings > Plugins (cards and detail pages).

Each icon is a 256 px PNG: a rounded-square app tile (superellipse, the shape the Plugins page
also uses for its monogram fallback) with a two-stop OKLab gradient, a soft top light and one
simple white glyph. Everything is drawn on a 1024 px canvas and box-filtered down, the same
supersample-then-reduce approach as scripts/app-icons/generate_app_icons.py, so edges are clean
at every size the app scales the PNG to.

Outputs (paths relative to the repo root):
  plugins/Noctis.Plugins.Mixxx/icon.png         turntable (official, amber)
  plugins/Noctis.Plugins.AlbumTown/icon.png     little cube creature with eyes (green)
  plugins/Noctis.Plugins.TrueShuffle/icon.png   crossing arrows and a sparkle (violet)
  plugins/Noctis.Plugins.LyricMotion/icon.png   letters flowing on a wave (blue)

Requires Pillow and numpy.  Run:  python scripts/plugin-icons/generate_plugin_icons.py
"""
from __future__ import annotations

import math
import os

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
FONT = os.path.join(ROOT, "src", "Noctis.UI", "Assets", "Fonts", "Inter-SemiBold.ttf")

SIZE = 256          # output edge
SS = 1024           # supersampled canvas edge (4x)
SQUIRCLE_N = 5.0    # superellipse exponent: rounder than a rounded rect, like an app tile


# ── colour (OKLab interpolation, as in the app icon generator) ──

def hexrgb(h: str) -> np.ndarray:
    h = h.lstrip("#")
    return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], dtype=np.float64) / 255.0


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055)


def srgb_to_oklab(c):
    l = srgb_to_linear(c)
    m1 = np.array([[0.4122214708, 0.5363325363, 0.0514459929],
                   [0.2119034982, 0.6806995451, 0.1073969566],
                   [0.0883024619, 0.2817188376, 0.6299787005]])
    lms = np.cbrt(l @ m1.T)
    m2 = np.array([[0.2104542553, 0.7936177850, -0.0040720468],
                   [1.9779984951, -2.4285922050, 0.4505937099],
                   [0.0259040371, 0.7827717662, -0.8086757660]])
    return lms @ m2.T


def oklab_to_srgb(lab):
    m2i = np.array([[1.0, 0.3963377774, 0.2158037573],
                    [1.0, -0.1055613458, -0.0638541728],
                    [1.0, -0.0894841775, -1.2914855480]])
    lms = (lab @ m2i.T) ** 3
    m1i = np.array([[4.0767416621, -3.3077115913, 0.2309699292],
                    [-1.2684380046, 2.6097574011, -0.3413193965],
                    [-0.0041960863, -0.7034186147, 1.7076147010]])
    return linear_to_srgb(lms @ m1i.T)


def gradient(t: np.ndarray, a: str, b: str) -> np.ndarray:
    la, lb = srgb_to_oklab(hexrgb(a)), srgb_to_oklab(hexrgb(b))
    t = np.clip(t, 0, 1)[..., None]
    return oklab_to_srgb(la * (1 - t) + lb * t)


# ── tile ──

def squircle_mask(size: int) -> np.ndarray:
    """Coverage of the superellipse |x|^n + |y|^n <= 1, 1 px antialiased at this size."""
    c = (size - 1) / 2.0
    y, x = np.mgrid[0:size, 0:size].astype(np.float64)
    u, v = np.abs((x - c) / c), np.abs((y - c) / c)
    r = (u ** SQUIRCLE_N + v ** SQUIRCLE_N) ** (1.0 / SQUIRCLE_N)
    edge = 1.5 / c
    return np.clip((1.0 - r) / edge + 0.5, 0, 1)


def tile(top: str, bottom: str) -> np.ndarray:
    """RGBA float tile: diagonal gradient, a soft light at the top, a faint shade at the bottom."""
    y, x = np.mgrid[0:SS, 0:SS].astype(np.float64) / (SS - 1)
    t = 0.75 * y + 0.25 * x                      # mostly top to bottom, a little across
    rgb = gradient(t, top, bottom)
    # top light: a wide ellipse of white just above the tile's upper edge
    d = np.sqrt(((x - 0.35) / 0.9) ** 2 + ((y + 0.15) / 0.55) ** 2)
    light = np.clip(1 - d, 0, 1) ** 1.6 * 0.22
    rgb = rgb + (1 - rgb) * light[..., None]
    shade = np.clip((y - 0.7) / 0.3, 0, 1) ** 2 * 0.10
    rgb = rgb * (1 - shade[..., None])
    alpha = squircle_mask(SS)
    return np.dstack([rgb, alpha])


def composite(base: np.ndarray, layer: Image.Image, opacity: float = 1.0) -> np.ndarray:
    """Alpha-over a white-ish RGBA PIL layer onto the float tile (inside the tile only)."""
    la = np.asarray(layer, dtype=np.float64) / 255.0
    a = la[..., 3:4] * opacity
    out = base.copy()
    out[..., :3] = base[..., :3] * (1 - a) + la[..., :3] * a
    return out


def glyph_shadow(layer: Image.Image, strength: float = 0.22) -> Image.Image:
    """A soft, short drop shadow under the glyph so white reads on the light top of the tile."""
    a = layer.split()[3].filter(ImageFilter.GaussianBlur(SS * 0.012))
    shadow = Image.new("RGBA", layer.size, (0, 0, 0, 0))
    shadow.putalpha(a.point(lambda v: int(v * strength)))
    out = Image.new("RGBA", layer.size, (0, 0, 0, 0))
    out.paste(shadow, (0, int(SS * 0.010)), shadow)
    return out


def finish(arr: np.ndarray, path: str) -> None:
    rgba = np.clip(arr, 0, 1)
    # premultiply for the box filter, then un-premultiply
    pm = rgba.copy()
    pm[..., :3] *= pm[..., 3:4]
    k = SS // SIZE
    pm = pm.reshape(SIZE, k, SIZE, k, 4).mean(axis=(1, 3))
    a = pm[..., 3:4]
    rgb = np.where(a > 1e-6, pm[..., :3] / np.maximum(a, 1e-6), 0)
    out = np.dstack([rgb, a])
    img = Image.fromarray((out * 255 + 0.5).astype(np.uint8), "RGBA")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path, optimize=True)
    print("wrote", os.path.relpath(path, ROOT))


def layer() -> tuple[Image.Image, ImageDraw.ImageDraw]:
    img = Image.new("RGBA", (SS, SS), (255, 255, 255, 0))
    return img, ImageDraw.Draw(img)


def S(v: float) -> float:
    """Unit (0..1) to canvas pixels."""
    return v * SS


def ring(d: ImageDraw.ImageDraw, cx, cy, r, w, fill=(255, 255, 255, 255)):
    d.ellipse([S(cx - r), S(cy - r), S(cx + r), S(cy + r)], outline=fill, width=int(S(w)))


def disc(d: ImageDraw.ImageDraw, cx, cy, r, fill=(255, 255, 255, 255)):
    d.ellipse([S(cx - r), S(cy - r), S(cx + r), S(cy + r)], fill=fill)


def stroke(d: ImageDraw.ImageDraw, pts, w, fill=(255, 255, 255, 255)):
    """Polyline with round caps and joins: discs stamped every half pixel along the path, so a
    thick curve has no notches where PIL's own wide lines join their short segments."""
    r = S(w) / 2
    px = [(S(x), S(y)) for x, y in pts]
    for (x0, y0), (x1, y1) in zip(px, px[1:]):
        n = max(1, int(math.hypot(x1 - x0, y1 - y0) * 2))
        for i in range(n + 1):
            x, y = x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n
            d.ellipse([x - r, y - r, x + r, y + r], fill=fill)


def bezier(p0, p1, p2, p3, n=64):
    out = []
    for i in range(n + 1):
        t = i / n
        mt = 1 - t
        out.append((mt ** 3 * p0[0] + 3 * mt * mt * t * p1[0] + 3 * mt * t * t * p2[0] + t ** 3 * p3[0],
                    mt ** 3 * p0[1] + 3 * mt * mt * t * p1[1] + 3 * mt * t * t * p2[1] + t ** 3 * p3[1]))
    return out


# ── glyphs ──

def mixxx() -> np.ndarray:
    """A turntable seen from above: the record with two grooves, its label and a tone arm."""
    base = tile("#FBBF24", "#EA580C")
    g, d = layer()
    cx, cy = 0.44, 0.55
    disc(d, cx, cy, 0.29)                                      # record
    alpha = np.asarray(g.split()[3], dtype=np.float64)
    holes, dh = layer()
    for r in (0.215, 0.165):                                   # grooves: faint, tile shows through
        ring(dh, cx, cy, r, 0.012)
    groove = np.asarray(holes.split()[3], dtype=np.float64) / 255.0
    label, dl = layer()
    disc(dl, cx, cy, 0.095)                                    # label: a clean hole
    hole = np.asarray(label.split()[3], dtype=np.float64) / 255.0
    alpha = alpha * (1 - groove * 0.45) * (1 - hole)
    g.putalpha(Image.fromarray(np.clip(alpha, 0, 255).astype(np.uint8)))
    disc(ImageDraw.Draw(g), cx, cy, 0.024)                     # spindle
    arm, da = layer()
    disc(da, 0.79, 0.24, 0.056)                                # pivot
    stroke(da, [(0.79, 0.24), (0.80, 0.50), (0.665, 0.665)], 0.036)
    hs = [(0.605, 0.640), (0.690, 0.715), (0.655, 0.755), (0.570, 0.680)]
    da.polygon([(S(x), S(y)) for x, y in hs], fill=(255, 255, 255, 255))
    both = Image.alpha_composite(g, arm)
    out = composite(base, glyph_shadow(both))
    out = composite(out, g)
    return composite(out, arm)


def album_town() -> np.ndarray:
    """A small cube creature: three shaded faces, two eyes and a smile."""
    base = tile("#6EE7B7", "#059669")
    g, d = layer()
    cx, top, w, h = 0.5, 0.20, 0.30, 0.17   # half width of the top diamond, its half height
    side = 0.34                               # height of the side faces
    T = [(cx, top), (cx + w, top + h), (cx, top + 2 * h), (cx - w, top + h)]
    L = [(cx - w, top + h), (cx, top + 2 * h), (cx, top + 2 * h + side), (cx - w, top + h + side)]
    R = [(cx, top + 2 * h), (cx + w, top + h), (cx + w, top + h + side), (cx, top + 2 * h + side)]
    for poly, a in ((L, 255), (R, 200), (T, 235)):
        d.polygon([(S(x), S(y)) for x, y in poly], fill=(255, 255, 255, a))
    out = composite(base, glyph_shadow(g))
    out = composite(out, g)
    # eyes on the front-left face (dark, with a white catch light) and a small smile
    e, de = layer()
    for ex in (cx - w * 0.68, cx - w * 0.28):
        ey = top + h + (ex - (cx - w)) / w * h + side * 0.40
        de.ellipse([S(ex - 0.040), S(ey - 0.055), S(ex + 0.040), S(ey + 0.055)], fill=(16, 54, 44, 255))
        de.ellipse([S(ex - 0.006), S(ey - 0.040), S(ex + 0.022), S(ey - 0.012)], fill=(255, 255, 255, 255))
    sx0, sx1 = cx - w * 0.62, cx - w * 0.34
    sy0 = top + h + (sx0 - (cx - w)) / w * h + side * 0.70
    sy1 = top + h + (sx1 - (cx - w)) / w * h + side * 0.70
    de.arc([S(sx0), S(min(sy0, sy1) - 0.03), S(sx1), S(max(sy0, sy1) + 0.03)], 20, 160,
           fill=(16, 54, 44, 255), width=int(S(0.016)))
    return composite(out, e)


def true_shuffle() -> np.ndarray:
    """Two crossing arrows (the shuffle sign) and a four-point sparkle."""
    base = tile("#C4B5FD", "#6D28D9")
    w = 0.062
    a = bezier((0.17, 0.36), (0.43, 0.36), (0.49, 0.70), (0.74, 0.70))
    b = bezier((0.17, 0.70), (0.43, 0.70), (0.49, 0.36), (0.74, 0.36))
    # b runs under a: b is cut by a widened copy of a around the crossing, then a goes on top
    under, du = layer()
    stroke(du, b, w)
    cutter, dc = layer()
    stroke(dc, a[24:41], w + 0.055)
    ua = np.asarray(under.split()[3], dtype=np.float64) * (1 - np.asarray(cutter.split()[3], dtype=np.float64) / 255.0)
    under.putalpha(Image.fromarray(ua.astype(np.uint8)))
    over, do = layer()
    stroke(do, a, w)
    for y in (0.36, 0.70):                                     # arrowheads
        do.polygon([(S(0.865), S(y)), (S(0.72), S(y - 0.10)), (S(0.72), S(y + 0.10))], fill=(255, 255, 255, 255))
    sx, sy, r = 0.27, 0.19, 0.085                              # sparkle, top left
    pts = []
    for i in range(8):
        ang = -math.pi / 2 + i * math.pi / 4
        rr = r if i % 2 == 0 else r * 0.26
        pts.append((S(sx + rr * math.cos(ang)), S(sy + rr * math.sin(ang))))
    do.polygon(pts, fill=(255, 255, 255, 255))
    g = Image.alpha_composite(under, over)
    out = composite(base, glyph_shadow(g))
    return composite(out, g)


def lyric_motion() -> np.ndarray:
    """Letters riding a wave, large to small, fading as they flow away."""
    base = tile("#93C5FD", "#4338CA")
    g = Image.new("RGBA", (SS, SS), (255, 255, 255, 0))

    def wave_y(x):
        return 0.60 + 0.075 * math.sin((x - 0.12) * 2 * math.pi / 0.62)

    def wave_slope(x):
        return 0.075 * 2 * math.pi / 0.62 * math.cos((x - 0.12) * 2 * math.pi / 0.62)

    # the wave itself: a thin line under the letters
    wl, dw = layer()
    pts = [(x / 100, wave_y(x / 100) + 0.085) for x in range(14, 88)]
    stroke(dw, pts, 0.022, fill=(255, 255, 255, 150))
    g = Image.alpha_composite(g, wl)
    letters = [("A", 0.25, 0.30, 255), ("a", 0.50, 0.22, 205), ("a", 0.71, 0.16, 150)]
    for ch, x, size, alpha in letters:
        font = ImageFont.truetype(FONT, int(S(size)))
        box = font.getbbox(ch)
        cw, chh = box[2] - box[0], box[3] - box[1]
        pad = int(S(0.05))
        tile_img = Image.new("RGBA", (cw + 2 * pad, chh + 2 * pad), (255, 255, 255, 0))
        ImageDraw.Draw(tile_img).text((pad - box[0], pad - box[1]), ch, font=font, fill=(255, 255, 255, alpha))
        ang = -math.degrees(math.atan(wave_slope(x)))
        rot = tile_img.rotate(ang, resample=Image.BICUBIC, expand=True)
        cy = wave_y(x)
        g.alpha_composite(rot, (int(S(x) - rot.width / 2), int(S(cy) - rot.height + S(0.06))))
    out = composite(base, glyph_shadow(g))
    return composite(out, g)


ICONS = [
    ("plugins/Noctis.Plugins.Mixxx/icon.png", mixxx),
    ("plugins/Noctis.Plugins.AlbumTown/icon.png", album_town),
    ("plugins/Noctis.Plugins.TrueShuffle/icon.png", true_shuffle),
    ("plugins/Noctis.Plugins.LyricMotion/icon.png", lyric_motion),
]


def main() -> None:
    for rel, make in ICONS:
        finish(make(), os.path.join(ROOT, rel))


if __name__ == "__main__":
    main()
