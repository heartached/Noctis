/**
 * Crops individual album covers out of the app screenshots for the Cover Flow
 * recreation. Coordinates were measured off the raw 2560x1380 captures.
 *
 * These are the same covers already visible in the product screenshots — the
 * section is a demo of the app's browser. If you want the site to carry no
 * third-party cover art at all, swap COVERS for your own images and rerun;
 * nothing else needs to change.
 */
import sharp from 'sharp';
import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { homedir } from 'node:os';

const here = dirname(fileURLToPath(import.meta.url));
const SRC_DIR = process.env.SHOTS_DIR || resolve(homedir(), 'Downloads/noctis-screenshots');
const OUT_DIR = resolve(here, '../public/covers');
const MANIFEST = resolve(here, '../src/data/covers.json');

// home.png "Recently Played": first cover at x=131 y=686, 470px square, pitch 475.
const HOME = (i) => ({ file: 'home.png', left: 131 + i * 475, top: 686, size: 470 });
// albums-grid.png rows: 486px square, pitch 486. Row 1 top=109, row 2 top=675.
const GRID = (col, row) => ({
  file: 'albums-grid.png',
  left: 107 + col * 486,
  top: row === 1 ? 109 : 675,
  size: 486,
});

const COVERS = [
  { slug: 'lover', title: 'Lover', artist: 'Taylor Swift', ...HOME(0) },
  { slug: 'evermore', title: 'evermore', artist: 'Taylor Swift', ...HOME(1) },
  { slug: 'loyal', title: 'LOYAL (Remix)', artist: 'PARTYNEXTDOOR, Drake & Bad Bunny', ...HOME(2) },
  { slug: 'party-never-ends', title: 'The Party Never Ends 2.0', artist: 'Juice WRLD', ...HOME(3) },
  { slug: 'debi-tirar', title: 'DeBÍ TiRAR MáS FOToS', artist: 'Bad Bunny', ...HOME(4) },
  { slug: 'fearless', title: 'Fearless', artist: 'Taylor Swift', ...GRID(2, 2) },
  { slug: '1989-tv', title: "1989 (Taylor's Version)", artist: 'Taylor Swift', ...GRID(3, 1) },
  { slug: 'fearless-platinum', title: 'Fearless (Platinum Edition)', artist: 'Taylor Swift', ...GRID(4, 2) },
];

const SIZES = [280, 560]; // 1x / 2x

await mkdir(OUT_DIR, { recursive: true });
await mkdir(dirname(MANIFEST), { recursive: true });

const manifest = [];

for (const c of COVERS) {
  const input = resolve(SRC_DIR, c.file);
  const variants = {};

  for (const [i, size] of SIZES.entries()) {
    const density = i === 0 ? '1x' : '2x';
    for (const fmt of ['avif', 'webp']) {
      const name = `${c.slug}-${size}.${fmt}`;
      await sharp(input)
        .extract({ left: c.left, top: c.top, width: c.size, height: c.size })
        .resize(size, size, { kernel: 'lanczos3' })
        [fmt](fmt === 'avif' ? { quality: 62, effort: 6 } : { quality: 82 })
        .toFile(resolve(OUT_DIR, name));
      variants[`${fmt}${density}`] = { src: `/covers/${name}`, width: size };
    }
  }

  manifest.push({
    slug: c.slug,
    title: c.title,
    artist: c.artist,
    width: SIZES[0],
    height: SIZES[0],
    avif: [variants.avif1x, variants.avif2x],
    webp: [variants.webp1x, variants.webp2x],
  });

  console.log(`${c.slug.padEnd(20)} ${c.title} — ${c.artist}`);
}

await writeFile(MANIFEST, JSON.stringify(manifest, null, 2) + '\n');
console.log(`\n${manifest.length} covers -> ${MANIFEST}`);
