/**
 * Palette-quantizes public/og-image.png in place (~75% smaller). Full-strength
 * dithering keeps the grain texture and the glow gradient from banding; social
 * cards render at ~500px wide, so the quantization is invisible where it counts.
 *
 * Runs automatically at the end of scripts/make-og.py.
 */
import sharp from 'sharp';
import { rename, stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const SRC = resolve(here, '../public/og-image.png');
const TMP = `${SRC}.tmp`;

const before = (await stat(SRC)).size;
await sharp(SRC).png({ palette: true, quality: 90, effort: 10, dither: 1.0 }).toFile(TMP);
await rename(TMP, SRC);
const after = (await stat(SRC)).size;
console.log(`og-image.png ${(before / 1024).toFixed(0)} KB -> ${(after / 1024).toFixed(0)} KB`);
