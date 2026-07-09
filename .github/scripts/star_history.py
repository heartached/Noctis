#!/usr/bin/env python3
"""Render this repository's star history as SVG (light + dark variants).

star-history.com can no longer chart repositories anonymously — GitHub
restricts stargazer data to a repo's own admins/collaborators — so the
old api.star-history.com embed in the README renders as a broken image
for every visitor. This script fetches starred_at timestamps with the
workflow's GITHUB_TOKEN (which acts as the repo and is therefore
allowed) and draws the cumulative chart itself. Stdlib only.

Usage: star_history.py <output-dir>
Writes <output-dir>/star-history.svg and star-history-dark.svg.
"""

import json
import math
import os
import sys
import urllib.request
from datetime import datetime, timedelta, timezone

REPO = os.environ.get("GITHUB_REPOSITORY", "heartached/Noctis")
TOKEN = os.environ["GITHUB_TOKEN"]

WIDTH, HEIGHT = 800, 420
MARGIN_L, MARGIN_R, MARGIN_T, MARGIN_B = 60, 28, 56, 48
PLOT_W = WIDTH - MARGIN_L - MARGIN_R
PLOT_H = HEIGHT - MARGIN_T - MARGIN_B
MAX_LINE_POINTS = 240
FONT = "ui-sans-serif, -apple-system, 'Segoe UI', Helvetica, Arial, sans-serif"

THEMES = {
    "star-history.svg": {
        "bg": "#ffffff", "border": "#d0d7de", "grid": "#eaeef2",
        "text": "#57606a", "title": "#24292f", "accent": "#8250df",
    },
    "star-history-dark.svg": {
        "bg": "#0d1117", "border": "#30363d", "grid": "#21262d",
        "text": "#8b949e", "title": "#e6edf3", "accent": "#a371f7",
    },
}


def fetch_star_times():
    times = []
    page = 1
    while page <= 400:  # the API hard-caps stargazer listing at 400 pages
        req = urllib.request.Request(
            f"https://api.github.com/repos/{REPO}/stargazers?per_page=100&page={page}",
            headers={
                "Accept": "application/vnd.github.star+json",
                "Authorization": f"Bearer {TOKEN}",
                "X-GitHub-Api-Version": "2022-11-28",
            },
        )
        with urllib.request.urlopen(req, timeout=30) as resp:
            batch = json.load(resp)
        if not batch:
            break
        times.extend(
            datetime.fromisoformat(s["starred_at"].replace("Z", "+00:00"))
            for s in batch
            if s.get("starred_at")
        )
        page += 1
    times.sort()
    return times


def nice_y_ticks(max_v):
    if max_v <= 5:
        return list(range(0, max(max_v, 1) + 1))
    raw = max_v / 4
    mag = 10 ** math.floor(math.log10(raw))
    step = next(m * mag for m in (1, 2, 5, 10) if raw <= m * mag)
    top = math.ceil(max_v / step) * step
    return [int(v) for v in range(0, int(top) + 1, int(step))]


def downsample(points):
    if len(points) <= MAX_LINE_POINTS:
        return points
    stride = math.ceil(len(points) / MAX_LINE_POINTS)
    kept = points[::stride]
    if kept[-1] != points[-1]:
        kept.append(points[-1])
    return kept


def render(times, theme):
    now = datetime.now(timezone.utc)
    total = len(times)
    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{WIDTH}" height="{HEIGHT}" '
        f'viewBox="0 0 {WIDTH} {HEIGHT}" font-family="{FONT}">',
        f'<rect x="0.5" y="0.5" width="{WIDTH - 1}" height="{HEIGHT - 1}" rx="8" '
        f'fill="{theme["bg"]}" stroke="{theme["border"]}"/>',
        f'<text x="{MARGIN_L}" y="32" font-size="17" font-weight="600" '
        f'fill="{theme["title"]}">&#9733; Star History &#8212; {REPO}</text>',
        f'<text x="{WIDTH - MARGIN_R}" y="32" font-size="12" text-anchor="end" '
        f'fill="{theme["text"]}">updated {now.strftime("%Y-%m-%d")}</text>',
    ]

    if not times:
        parts.append(
            f'<text x="{WIDTH / 2}" y="{HEIGHT / 2}" font-size="15" text-anchor="middle" '
            f'fill="{theme["text"]}">No stars yet &#8212; be the first &#9733;</text></svg>'
        )
        return "".join(parts)

    t0, t1 = times[0], now
    if t1 - t0 < timedelta(days=1):
        t0 = t1 - timedelta(days=1)
    span = (t1 - t0).total_seconds()

    def x(t):
        return MARGIN_L + (t - t0).total_seconds() / span * PLOT_W

    y_ticks = nice_y_ticks(total)
    y_max = y_ticks[-1]

    def y(v):
        return MARGIN_T + PLOT_H - v / y_max * PLOT_H

    for v in y_ticks:
        parts.append(
            f'<line x1="{MARGIN_L}" y1="{y(v):.1f}" x2="{WIDTH - MARGIN_R}" y2="{y(v):.1f}" '
            f'stroke="{theme["grid"]}"/>'
            f'<text x="{MARGIN_L - 8}" y="{y(v) + 4:.1f}" font-size="12" text-anchor="end" '
            f'fill="{theme["text"]}">{v}</text>'
        )

    date_fmt = "%b %Y" if t1 - t0 > timedelta(days=300) else "%b %d"
    for k in range(4):
        t = t0 + (t1 - t0) * k / 3
        anchor = ("start", "middle", "middle", "end")[k]
        parts.append(
            f'<text x="{x(t):.1f}" y="{HEIGHT - MARGIN_B + 24}" font-size="12" '
            f'text-anchor="{anchor}" fill="{theme["text"]}">{t.strftime(date_fmt)}</text>'
        )

    points = downsample([(t, i + 1) for i, t in enumerate(times)] + [(now, total)])
    coords = " ".join(f"{x(t):.1f},{y(v):.1f}" for t, v in points)
    base = MARGIN_T + PLOT_H
    first_x, last_x = x(points[0][0]), x(points[-1][0])
    parts.append(
        f'<linearGradient id="fill" x1="0" y1="0" x2="0" y2="1">'
        f'<stop offset="0" stop-color="{theme["accent"]}" stop-opacity="0.28"/>'
        f'<stop offset="1" stop-color="{theme["accent"]}" stop-opacity="0"/>'
        f'</linearGradient>'
        f'<polygon points="{first_x:.1f},{base:.1f} {coords} {last_x:.1f},{base:.1f}" fill="url(#fill)"/>'
        f'<polyline points="{coords}" fill="none" stroke="{theme["accent"]}" '
        f'stroke-width="2.5" stroke-linejoin="round" stroke-linecap="round"/>'
        f'<circle cx="{last_x:.1f}" cy="{y(total):.1f}" r="4" fill="{theme["accent"]}"/>'
        f'<text x="{last_x - 8:.1f}" y="{y(total) - 10:.1f}" font-size="13" font-weight="600" '
        f'text-anchor="end" fill="{theme["title"]}">{total} &#9733;</text>'
        f'</svg>'
    )
    return "".join(parts)


def main():
    out_dir = sys.argv[1]
    os.makedirs(out_dir, exist_ok=True)
    times = fetch_star_times()
    print(f"{REPO}: {len(times)} stars")
    for name, theme in THEMES.items():
        path = os.path.join(out_dir, name)
        with open(path, "w", encoding="utf-8") as f:
            f.write(render(times, theme))
        print(f"wrote {path}")


if __name__ == "__main__":
    main()
