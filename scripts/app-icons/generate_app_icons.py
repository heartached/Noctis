#!/usr/bin/env python3
"""Generates Noctis's app icons: the classic red disc plus ten gradient/frosted variants.

Every output size is rendered on its own from the vector-ish description below (analytic
disc, analytic gradients, the note glyph taken from the 2000 px source logo) at a ~2048 px
supersampled canvas and box-filtered down to the exact pixel size, so a 16 px frame is a
true 16 px rendering, not a resample of a resample. Small frames drop the frosted/shadow
effects (they only muddy 16-32 px) and get a light unsharp mask.

Outputs (paths relative to the repo root):
  src/Noctis/Assets/AppIcons/<id>.ico      multi-size icon (16..256, all DPI steps)
  src/Noctis/Assets/AppIcons/<id>.png      in-app preview (PREVIEW_SIZE px)
  src/Noctis/Assets/Icons/Noctis.ico       classic icon (exe, installer)
  src/Noctis.UI/Assets/Icons/Noctis.ico    classic icon (window/tray fallback)
  artifacts/app-icons/<id>-1024.png        1024 px masters (not committed)
  artifacts/app-icons/icon-sheet.png       contact sheet of all ten

Requires Pillow and numpy.  Run:  python scripts/app-icons/generate_app_icons.py
"""
from __future__ import annotations

import io
import math
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
SOURCE = os.path.join(HERE, "noctis-logo-source.png")  # 2000x2000 RGBA, red disc + white note

# Windows asks for 16/24/32 x DPI scale (100..300 %): include every step so the shell never
# stretches a neighbouring frame.
ICO_SIZES = [16, 20, 24, 28, 30, 32, 36, 40, 42, 48, 56, 60, 64, 72, 80, 96, 128, 256]
PREVIEW_SIZE = 96   # shown at 48 DIP (2:1 at 100 %, 1:1 at 200 %)
SS_TARGET = 1024        # supersampled canvas edge (>= 4x for every frame)

CLASSIC_RED = (231, 72, 86)  # #E74856, sampled from the source logo


def hexrgb(h: str) -> np.ndarray:
    h = h.lstrip("#")
    return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], dtype=np.float64) / 255.0


# ── colour helpers (OKLab interpolation keeps gradients from going muddy in the middle) ──

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


def gradient(t: np.ndarray, stops) -> np.ndarray:
    """Multi-stop gradient in OKLab. t: HxW in [0,1]; stops: [(pos, '#hex'), ...]."""
    pos = np.array([p for p, _ in stops])
    labs = np.array([srgb_to_oklab(hexrgb(c)) for _, c in stops])
    t = np.clip(t, 0, 1)
    out = np.empty(t.shape + (3,))
    for ch in range(3):
        out[..., ch] = np.interp(t, pos, labs[:, ch])
    return oklab_to_srgb(out)


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


# ── variants ──
# bg:     linear gradient (angle in degrees, 0 = left->right, 90 = top->bottom) + stops
# blobs:  soft radial colour glows (cx, cy, radius, '#hex', strength), unit coords over the disc
# note:   note colour stops along y (top->bottom), opacity top/bottom (frost), glow colour
# rim:    top inner highlight strength, bottom inner shade strength
VARIANTS = [
    dict(id="classic", name="Classic", flat=CLASSIC_RED),
    # Classic inverted: the red note on a near-black disc, like a system dark-mode icon.
    dict(id="dark", name="Dark", dark=True,
         bg=(100, [(0.0, "#2A2A2E"), (1.0, "#0E0E10")]),
         note=dict(stops=[(0.0, "#F2606F"), (1.0, "#E23E52")], top=1.0, bottom=1.0, white_edge=0.0),
         glow=("#E74856", 0.18), shadow=("#000000", 0.35), rim=(0.14, 0.0)),
    dict(id="blush", name="Blush",
         bg=(120, [(0.0, "#FFFFFF"), (0.55, "#FFF1F3"), (1.0, "#F8C9D2")]),
         blobs=[(0.78, 0.86, 0.55, "#F3A6B6", 0.45)],
         note=dict(stops=[(0.0, "#F2667B"), (1.0, "#DE3E55")], top=1.0, bottom=1.0, white_edge=0.0),
         shadow=("#B0344A", 0.22), rim=(0.55, 0.06)),
    dict(id="sky", name="Sky",
         bg=(110, [(0.0, "#DDEEFF"), (0.5, "#A9D2F7"), (1.0, "#6FA9EA")]),
         blobs=[(0.2, 0.15, 0.5, "#FFFFFF", 0.55)],
         note=dict(stops=[(0.0, "#FFFFFF"), (1.0, "#F4F9FF")], top=1.0, bottom=0.9, white_edge=0.25),
         shadow=("#245E9E", 0.38), rim=(0.45, 0.08)),
    dict(id="sunset", name="Sunset",
         bg=(125, [(0.0, "#FFC06A"), (0.45, "#FF7A7E"), (1.0, "#E23B52")]),
         blobs=[(0.15, 0.1, 0.55, "#FFE0A0", 0.45)],
         note=dict(stops=[(0.0, "#FFFFFF"), (1.0, "#FFF4F0")], top=1.0, bottom=0.9, white_edge=0.25),
         shadow=("#8A1730", 0.30), rim=(0.35, 0.10)),
    dict(id="violet", name="Violet",
         bg=(120, [(0.0, "#E2C8FF"), (0.5, "#9B86F7"), (1.0, "#5160DC")]),
         blobs=[(0.85, 0.2, 0.45, "#7FB2FF", 0.35)],
         note=dict(stops=[(0.0, "#FFFFFF"), (1.0, "#F3F0FF")], top=1.0, bottom=0.9, white_edge=0.25),
         shadow=("#2B2A8C", 0.30), rim=(0.35, 0.10)),
    dict(id="aurora", name="Aurora",
         bg=(60, [(0.0, "#2FC2BE"), (0.5, "#9EDFC6"), (1.0, "#FFB894")]),
         blobs=[(0.92, 0.92, 0.45, "#FF9C86", 0.30), (0.08, 0.08, 0.45, "#22AFC2", 0.30)],
         note=dict(stops=[(0.0, "#FFFFFF"), (1.0, "#FFF8F2")], top=1.0, bottom=0.9, white_edge=0.25),
         shadow=("#1F6E73", 0.28), rim=(0.35, 0.08)),
    dict(id="midnight", name="Midnight", dark=True,
         bg=(100, [(0.0, "#0A1228"), (0.6, "#14285E"), (1.0, "#2850B8")]),
         blobs=[(0.5, 1.05, 0.6, "#3B82F6", 0.55)],
         note=dict(stops=[(0.0, "#FFFFFF"), (1.0, "#DCE8FF")], top=1.0, bottom=0.88, white_edge=0.3),
         glow=("#5B9BFF", 0.40), shadow=("#000000", 0.35), rim=(0.18, 0.0)),
    dict(id="graphite", name="Graphite", dark=True,
         bg=(100, [(0.0, "#36363B"), (1.0, "#121214")]),
         blobs=[(0.5, 0.56, 0.42, "#E74856", 0.55)],
         note=dict(stops=[(0.0, "#FFFFFF"), (1.0, "#F2E6E8")], top=1.0, bottom=0.9, white_edge=0.3),
         glow=("#FF4D5E", 0.55), shadow=("#000000", 0.30), rim=(0.16, 0.0)),
    dict(id="ruby", name="Ruby", dark=True,
         bg=(115, [(0.0, "#C0283E"), (0.55, "#7E1226"), (1.0, "#3E0712")]),
         blobs=[(0.2, 0.12, 0.45, "#FF6F7F", 0.40)],
         note=dict(stops=[(0.0, "#FFFFFF"), (1.0, "#FFE6EA")], top=1.0, bottom=0.9, white_edge=0.3),
         glow=("#FF5A6E", 0.30), shadow=("#1A0006", 0.40), rim=(0.25, 0.0)),
    dict(id="neon", name="Neon", dark=True,
         bg=(110, [(0.0, "#1C0F3A"), (1.0, "#08040F")]),
         blobs=[(0.5, 0.55, 0.5, "#9B3CFF", 0.60), (0.85, 0.15, 0.35, "#FF4FD8", 0.30)],
         note=dict(stops=[(0.0, "#FFFFFF"), (1.0, "#F1E4FF")], top=1.0, bottom=0.9, white_edge=0.3),
         glow=("#C26BFF", 0.70), shadow=("#000000", 0.30), rim=(0.16, 0.0)),
]


# ── source glyph ──

_NOTE_SRC = None


def note_source() -> Image.Image:
    """The white note of the source logo as a float mask (mode F, 2000x2000)."""
    global _NOTE_SRC
    if _NOTE_SRC is None:
        a = np.asarray(Image.open(SOURCE).convert("RGBA")).astype(np.float64)
        # Red disc is (231,72,86); the note is pure white. Green runs 72 -> 255 across the
        # note's antialiased edge, so it gives the note coverage directly.
        m = np.clip((a[..., 1] - CLASSIC_RED[1]) / (255.0 - CLASSIC_RED[1]), 0, 1)
        m *= a[..., 3] / 255.0
        _NOTE_SRC = Image.fromarray(m.astype(np.float32), mode="F")
    return _NOTE_SRC


def blur(arr: np.ndarray, sigma: float) -> np.ndarray:
    """Gaussian approximation: three separable box passes (sizes per Kovesi)."""
    if sigma <= 0.3:
        return arr
    n = 3
    wi = math.sqrt(12 * sigma * sigma / n + 1)
    wl = int(wi) - (1 - int(wi) % 2)
    m = round((12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4 * wl - 4))
    out = arr.astype(np.float64)
    for i in range(n):
        rad = ((wl if i < m else wl + 2) - 1) // 2
        for ax in (0, 1):
            out = _box1d(out, rad, ax)
    return out


def _box1d(a: np.ndarray, rad: int, axis: int) -> np.ndarray:
    if rad <= 0:
        return a
    pad = [(0, 0), (0, 0)]
    pad[axis] = (rad + 1, rad)
    p = np.pad(a, pad, mode="constant")
    cs = np.cumsum(p, axis=axis)
    hi = np.take(cs, np.arange(2 * rad + 1, cs.shape[axis]), axis=axis)
    lo = np.take(cs, np.arange(0, cs.shape[axis] - 2 * rad - 1), axis=axis)
    return (hi - lo) / (2 * rad + 1)


def shift_down(arr: np.ndarray, dy: int) -> np.ndarray:
    if dy <= 0:
        return arr
    out = np.zeros_like(arr)
    out[dy:] = arr[:-dy]
    return out


# ── renderer ──

def render(v: dict, size: int) -> Image.Image:
    """Renders variant v at exactly size x size pixels (RGBA)."""
    ss = max(2, math.ceil(SS_TARGET / size))
    n = size * ss
    # Half a target pixel of breathing room keeps the disc's 12/3/6/9 o'clock edges
    # antialiased instead of flat-clipped by the canvas (the old icon's flat spots).
    margin = 0.5 if size <= 64 else size / 128.0
    r = (size / 2.0 - margin) * ss
    c = n / 2.0
    yy, xx = np.mgrid[0:n, 0:n].astype(np.float64) + 0.5
    dist = np.hypot(xx - c, yy - c)
    coverage = np.clip(r - dist + 0.5, 0, 1)
    # unit coords over the disc's bounding box
    u = (xx - (c - r)) / (2 * r)
    w = (yy - (c - r)) / (2 * r)

    # note mask scaled to the disc (the source note is drawn on a full-bleed 2000 px disc)
    d = int(round(2 * r))
    note_im = note_source().resize((d, d), Image.LANCZOS)
    note = np.zeros((n, n))
    o = int(round(c - d / 2.0))
    note[o:o + d, o:o + d] = np.clip(np.asarray(note_im, dtype=np.float64), 0, 1)

    # effects fade in with size: 16-24 px frames get a clean gradient + solid note
    fx = float(smoothstep(24, 72, size))

    if "flat" in v:
        col = np.empty((n, n, 3))
        col[:] = np.array(v["flat"]) / 255.0
        col = col * (1 - note[..., None]) + note[..., None]
    else:
        ang = math.radians(v["bg"][0])
        t = (u - 0.5) * math.cos(ang) + (w - 0.5) * math.sin(ang)
        t = t / (abs(math.cos(ang)) * 0.5 + abs(math.sin(ang)) * 0.5) * 0.5 + 0.5
        col = gradient(t, v["bg"][1])
        for (bx, by, br, bc, bs) in v.get("blobs", []):
            k = np.exp(-(((u - bx) ** 2 + (w - by) ** 2) / (br * br)) * 2.2) * bs
            col = col * (1 - k[..., None]) + hexrgb(bc) * k[..., None]

        rr = dist / r  # 0 centre .. 1 edge
        hi, lo = v.get("rim", (0.3, 0.05))
        # top inner highlight (light falling on a glass tile) and a soft bottom inner shade
        edge = smoothstep(0.72, 1.0, rr)
        top = np.clip(1 - w * 1.6, 0, 1)
        bot = np.clip((w - 0.45) * 1.8, 0, 1)
        k = edge * top * hi * fx
        col = col * (1 - k[..., None]) + k[..., None]
        k = edge * bot * lo * fx
        col = col * (1 - k[..., None])

        # glow behind the note (dark variants)
        if "glow" in v:
            gc, gs = v["glow"]
            g = blur(note, 0.045 * d) * gs * (0.35 + 0.65 * fx)
            g = np.clip(g * 1.6, 0, 1)
            gcol = hexrgb(gc)
            col = 1 - (1 - col) * (1 - gcol * g[..., None])  # screen blend

        # soft drop shadow under the note
        sc, sa = v.get("shadow", ("#000000", 0.25))
        if fx > 0:
            sh = blur(shift_down(note, int(0.018 * d)), 0.028 * d) * sa * fx
            col = col * (1 - sh[..., None]) + hexrgb(sc) * sh[..., None]

        # frosted note: vertical colour + opacity ramp, bright top edge
        nv = v["note"]
        ncol = gradient(np.clip((w - 0.22) / 0.56, 0, 1), nv["stops"])
        op_top, op_bot = nv["top"], nv["bottom"]
        op = op_top + (op_bot - op_top) * np.clip((w - 0.22) / 0.56, 0, 1)
        op = op * fx + 1.0 * (1 - fx)  # small sizes: fully opaque for legibility
        a = note * op
        frosted = col * 0.35 + 0.65  # the tile seen through frosted white glass
        body = ncol * 0.92 + frosted * 0.08 if nv.get("white_edge", 0) > 0 else ncol
        col = col * (1 - a[..., None]) + body * a[..., None]
        if nv.get("white_edge", 0) > 0 and fx > 0:
            rim_px = max(1, int(0.006 * d))
            lip = np.clip(note - shift_down(note, rim_px), 0, 1) * nv["white_edge"] * fx
            col = col * (1 - lip[..., None]) + lip[..., None]

    # box-filter down to the target size; colour is premultiplied by disc coverage
    def down(x):
        return x.reshape(size, ss, size, ss, *x.shape[2:]).mean(axis=(1, 3))

    alpha = down(coverage)
    prem = down(col * coverage[..., None])
    plain = down(col)
    rgb = np.where(alpha[..., None] > 1e-6, prem / np.maximum(alpha[..., None], 1e-6), plain)

    if size <= 48:
        # light unsharp mask for small frames (RGB only; alpha stays the exact coverage)
        im = Image.fromarray((np.clip(rgb, 0, 1) * 255 + 0.5).astype(np.uint8), "RGB")
        amount = 60 if size <= 24 else 40
        im = im.filter(ImageFilter.UnsharpMask(radius=0.6, percent=amount, threshold=0))
        rgb8 = np.asarray(im)
    else:
        # tiny triangular dither breaks up 8-bit banding in the long gradients
        # (big frames only: below 128 px a gradient step is wider than a level anyway,
        # and noise would just bloat the PNG frames). Flat classic needs none.
        noise = 0.0
        if size >= 128 and "flat" not in v:
            rng = np.random.default_rng(size)
            noise = (rng.random(rgb.shape) - rng.random(rgb.shape)) / 255.0
        rgb8 = (np.clip(rgb + noise, 0, 1) * 255 + 0.5).astype(np.uint8)
    a8 = (np.clip(alpha, 0, 1) * 255 + 0.5).astype(np.uint8)
    return Image.fromarray(np.dstack([rgb8, a8]), "RGBA")


def png_bytes(im: Image.Image) -> bytes:
    b = io.BytesIO()
    im.save(b, format="PNG", optimize=True)
    return b.getvalue()


def write_ico(path: str, frames: list[Image.Image]) -> None:
    """ICO with one PNG-compressed 32-bit frame per size (Vista+ format)."""
    datas = [png_bytes(f) for f in frames]
    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(frames)))
    offset = 6 + 16 * len(frames)
    for f, dat in zip(frames, datas):
        w, h = f.size
        out.write(struct.pack("<BBBBHHII", w & 0xFF, h & 0xFF, 0, 0, 1, 32, len(dat), offset))
        offset += len(dat)
    for dat in datas:
        out.write(dat)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as fh:
        fh.write(out.getvalue())


def contact_sheet(masters: list[tuple[dict, Image.Image]], path: str) -> None:
    cell, pad, label = 220, 28, 34
    cols = 5
    rows = math.ceil(len(masters) / cols)
    W = cols * cell + (cols + 1) * pad
    H = rows * (cell + label) + (rows + 1) * pad
    sheet = Image.new("RGBA", (W, H * 2), (0, 0, 0, 0))
    try:
        font = ImageFont.truetype("segoeui.ttf", 20)
    except OSError:
        font = ImageFont.load_default()
    for half, bgc, fg in ((0, (246, 246, 248, 255), (40, 40, 44)), (1, (22, 22, 24, 255), (225, 225, 230))):
        ImageDraw.Draw(sheet).rectangle([0, half * H, W, half * H + H], fill=bgc)
        for i, (v, im) in enumerate(masters):
            x = pad + (i % cols) * (cell + pad)
            y = half * H + pad + (i // cols) * (cell + label + pad)
            sheet.alpha_composite(im.resize((cell, cell), Image.LANCZOS), (x, y))
            dr = ImageDraw.Draw(sheet)
            tw = dr.textlength(v["name"], font=font)
            dr.text((x + (cell - tw) / 2, y + cell + 6), v["name"], fill=fg, font=font)
    sheet.convert("RGB").save(path, optimize=True)


def main(argv: list[str]) -> int:
    only = set(argv[1:])
    icon_dir = os.path.join(ROOT, "src", "Noctis", "Assets", "AppIcons")
    master_dir = os.path.join(ROOT, "artifacts", "app-icons")
    os.makedirs(icon_dir, exist_ok=True)
    os.makedirs(master_dir, exist_ok=True)
    masters = []
    for v in VARIANTS:
        if only and v["id"] not in only:
            continue
        frames = [render(v, s) for s in ICO_SIZES]
        write_ico(os.path.join(icon_dir, v["id"] + ".ico"), frames)
        render(v, PREVIEW_SIZE).save(os.path.join(icon_dir, v["id"] + ".png"), optimize=True)
        m = render(v, 1024)
        m.save(os.path.join(master_dir, v["id"] + "-1024.png"))
        masters.append((v, m))
        if v["id"] == "classic":
            write_ico(os.path.join(ROOT, "src", "Noctis", "Assets", "Icons", "Noctis.ico"), frames)
            write_ico(os.path.join(ROOT, "src", "Noctis.UI", "Assets", "Icons", "Noctis.ico"), frames)
        print("rendered", v["id"])
    if not only:
        contact_sheet(masters, os.path.join(master_dir, "icon-sheet.png"))
        contact_sheet(masters, os.path.join(ROOT, "icon-sheet.png"))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
