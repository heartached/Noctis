"""
Captures /og as public/og-image.png at exactly 1200x630.

Run against a served build:
    npm run build:fast && npm run preview     # in one shell
    python scripts/make-og.py                 # in another

The result is committed, so CI never needs a browser.
"""
import pathlib
import subprocess
import sys

from playwright.sync_api import sync_playwright

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "public" / "og-image.png"
URL = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:4321/og"

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    page = browser.new_page(viewport={"width": 1200, "height": 630},
                            device_scale_factor=1)
    page.goto(URL, wait_until="networkidle")
    # Webfonts must be settled or the card renders in the fallback stack.
    page.evaluate("document.fonts.ready")
    page.wait_for_timeout(600)
    page.screenshot(path=str(OUT))
    browser.close()

# Chromium writes a 24-bit PNG; the quantize pass keeps the committed file small.
subprocess.run(["node", str(ROOT / "scripts" / "optimize-og.mjs")], check=True)

size = OUT.stat().st_size
print(f"wrote {OUT}  ({size / 1024:.0f} KB)")
if size < 5_000:
    print("! suspiciously small — check the /og route rendered")
    sys.exit(1)
