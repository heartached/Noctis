/**
 * Downloads the real application icons used by the comparison tables and the
 * download cards, and writes square PNGs to public/logos.
 *
 * Run once; the output is committed. Same contract as fetch-fonts.mjs — the
 * site itself makes no third-party requests at runtime, and a vendor moving
 * their icon cannot break a build.
 *
 * Only marks that are genuinely full-colour live here. The ones that are
 * MONOCHROME BY DESIGN — the Apple mark and the foobar2000 moth — stay as
 * inline currentColor vectors in AppMark.astro, because a fixed black raster
 * disappears on the dark theme and a fixed white one disappears on the light.
 * Spotify, Apple Music, Windows and Noctis stay vector too: their official
 * geometry is simple enough to draw exactly, and vector beats any raster.
 *
 * Sources are each project's own repository or site — never an icon-pack site.
 */
import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import sharp from 'sharp';

const here = dirname(fileURLToPath(import.meta.url));
const OUT = resolve(here, '../public/logos');

/**
 * 8x the 32px the marks render at. Every source below is at least this big, so
 * nothing is upscaled, and it leaves headroom if a mark is ever shown larger.
 */
const SIZE = 256;

const UA = 'Mozilla/5.0 (compatible; noctis-site-icons/1.0; +https://noctisapp.cc)';

const ICONS = [
  {
    name: 'musicbee',
    // MusicBee ships no vector and the site's <img> is 64px. The favicon is a
    // multi-image .ico whose largest entry is a 256px PNG.
    url: 'https://getmusicbee.com/favicon.ico',
    ico: true,
  },
  {
    name: 'harmonoid',
    // The ROUND icon, not the macOS squircle in the same repo: this is the mark
    // harmonoid.com serves as its own favicon, so it is the primary one.
    // Commit-pinned (was /master/) so the fetched bytes can't silently change.
    url: 'https://raw.githubusercontent.com/harmonoid/harmonoid/78759d11c881b566dac01356f7b8a3eddf4ef0d4/linux/debian/usr/share/icons/hicolor/256x256/apps/harmonoid.png',
  },
  {
    name: 'strawberry',
    url: 'https://raw.githubusercontent.com/strawberrymusicplayer/strawberry/7b5d784bb125743a54147bf3b17b4eb0dd0eb322/data/icons/full/strawberry.png',
  },
  {
    name: 'applemusic',
    // The current iOS 26 icon. Raster rather than a hand-drawn path because the
    // shipping icon is a gradient tile with a glossy, highlighted note — a flat
    // two-tone vector of it is a different, older icon, not a simplification.
    url: 'https://upload.wikimedia.org/wikipedia/commons/f/f8/Apple_Music_icon_iOS_26.svg',
    density: 400,
    // Already a full-bleed tile; trimming would only shave its own soft edge.
    trim: false,
  },
  {
    name: 'tux',
    // lewing@isc.tamu.edu's Tux, vectorised. The canonical Linux mascot; the
    // hand-drawn penguin this replaces read as a smudge at any size.
    url: 'https://upload.wikimedia.org/wikipedia/commons/3/35/Tux.svg',
    density: 512,
  },
];

/**
 * Pulls the largest PNG-encoded image out of a Windows .ico.
 * Layout: 6-byte header, then one 16-byte directory entry per image. A width
 * or height byte of 0 means 256 — the sizes we actually want.
 */
function largestFromIco(buf) {
  const count = buf.readUInt16LE(4);
  let best = null;

  for (let i = 0; i < count; i++) {
    const at = 6 + i * 16;
    const w = buf.readUInt8(at) || 256;
    const h = buf.readUInt8(at + 1) || 256;
    const bytes = buf.readUInt32LE(at + 8);
    const offset = buf.readUInt32LE(at + 12);
    if (!best || w * h > best.w * best.h) best = { w, h, bytes, offset };
  }

  if (!best) throw new Error('no images in ico');
  const data = buf.subarray(best.offset, best.offset + best.bytes);
  const isPng = data.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
  if (!isPng) throw new Error(`largest ico entry (${best.w}x${best.h}) is BMP, not PNG`);
  return { data, w: best.w, h: best.h };
}

await mkdir(OUT, { recursive: true });

for (const icon of ICONS) {
  const res = await fetch(icon.url, { headers: { 'User-Agent': UA } });
  if (!res.ok) throw new Error(`${icon.name}: ${res.status} ${res.statusText}`);
  let buf = Buffer.from(await res.arrayBuffer());

  let note = '';
  if (icon.ico) {
    const { data, w, h } = largestFromIco(buf);
    buf = data;
    note = ` (from ${w}x${h} ico entry)`;
  }

  let input = icon.density ? sharp(buf, { density: icon.density }) : sharp(buf);

  // Vendors pad their icons by different amounts. Trimming to the artwork first
  // is what makes marks of five different shapes read as one row.
  if (icon.trim !== false) input = input.trim();

  /* WebP, not PNG. Half of these are photographic or gradient artwork — a
     strawberry, a gradient tile, a glossy note — where a palette PNG bands and
     a truecolour PNG runs to 100 KB for one 32px icon. WebP with alpha holds
     the gradients at a fifth of that, and it is already this site's baseline:
     the covers and screenshots fall back to WebP, not to PNG or JPEG. */
  const webp = await input
    .resize(SIZE, SIZE, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
    .webp({ quality: 92, effort: 6 })
    .toBuffer();

  const file = resolve(OUT, `${icon.name}.webp`);
  await writeFile(file, webp);
  console.log(`${icon.name.padEnd(12)} ${SIZE}x${SIZE}  ${(webp.length / 1024).toFixed(1)} KB${note}`);
}

console.log(`\nwrote ${OUT}`);
