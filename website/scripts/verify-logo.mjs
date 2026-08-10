/**
 * Verifies the hand-traced logo SVG against the original raster mark.
 * Renders the SVG at 2000x2000 and reports the percentage of pixels whose
 * "is the note, or is the disc" classification disagrees with the source PNG.
 */
import sharp from 'sharp';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { homedir } from 'node:os';

const here = dirname(fileURLToPath(import.meta.url));
const SRC_PNG =
  process.env.NOCTIS_LOGO_PNG ||
  resolve(homedir(), 'Downloads/Noctis/Noctis/src/Noctis/Assets/Icons/Noctis Logo Clean.png');
const SVG = resolve(here, '../src/assets/logo-mark.svg');

const SIZE = 500; // compare downsampled; sub-pixel AA noise is not interesting

async function classify(buf) {
  const { data } = await sharp(buf)
    .resize(SIZE, SIZE, { fit: 'fill' })
    .flatten({ background: '#000000' })
    .raw()
    .toBuffer({ resolveWithObject: true });
  // 0 = background/disc, 1 = white note
  const out = new Uint8Array(SIZE * SIZE);
  for (let i = 0, p = 0; i < data.length; i += 3, p++) {
    out[p] = data[i] > 175 && data[i + 1] > 175 && data[i + 2] > 175 ? 1 : 0;
  }
  return out;
}

const [origBuf, svgBuf] = await Promise.all([readFile(SRC_PNG), readFile(SVG)]);
const [a, b] = await Promise.all([classify(origBuf), classify(svgBuf)]);

let diff = 0;
let noteA = 0;
for (let i = 0; i < a.length; i++) {
  if (a[i] !== b[i]) diff++;
  if (a[i]) noteA++;
}
const pct = (diff / a.length) * 100;
console.log(`note pixels in original : ${noteA} (${((noteA / a.length) * 100).toFixed(2)}%)`);
console.log(`mismatched pixels       : ${diff} (${pct.toFixed(3)}%)`);
console.log(pct < 0.6 ? 'PASS — trace matches the original mark' : 'FAIL — trace needs adjusting');

// Write a visual diff so a human can eyeball it too.
const rgb = Buffer.alloc(SIZE * SIZE * 3);
for (let i = 0; i < a.length; i++) {
  const o = i * 3;
  if (a[i] === b[i]) {
    const v = a[i] ? 235 : 30;
    rgb[o] = v; rgb[o + 1] = v; rgb[o + 2] = v;
  } else {
    rgb[o] = 255; rgb[o + 1] = 40; rgb[o + 2] = 40; // red = disagreement
  }
}
const outPath = process.env.LOGO_DIFF_OUT || resolve(here, '../.logo-diff.png');
await sharp(rgb, { raw: { width: SIZE, height: SIZE, channels: 3 } }).png().toFile(outPath);
console.log(`diff image -> ${outPath}`);
