/**
 * Builds the responsive image set: AVIF with a WebP fallback, at 1x and 2x,
 * plus the favicon set from the traced logo mark.
 *
 * Emits src/data/images.json so every <img> can carry real intrinsic
 * width/height and contribute zero layout shift.
 */
import sharp from 'sharp';
import { mkdir, writeFile, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, basename, extname } from 'node:path';
import { homedir } from 'node:os';

const here = dirname(fileURLToPath(import.meta.url));
const SRC_DIR = process.env.SHOTS_DIR || resolve(homedir(), 'Downloads/noctis-screenshots');
const OUT_DIR = resolve(here, '../public/shots');
const ICON_DIR = resolve(here, '../public/icons');
const LOGO_SVG = resolve(here, '../src/assets/logo-mark.svg');
const MASKABLE_SVG = resolve(here, '../src/assets/logo-maskable.svg');
const MANIFEST = resolve(here, '../src/data/images.json');

/**
 * Roles decide the emitted widths. 1x first, then 2x.
 * `hero` is the only eagerly-loaded image, so it gets the widest pair.
 */
const ROLES = {
  hero: [1280, 2560],
  showcase: [1100, 2200],
  card: [640, 1280],
};

/** screenshot -> role + alt text. Alt is real description, never the filename. */
const SHOTS = {
  'home.png': { role: 'hero', alt: 'The Noctis home screen showing recently played albums and the playback bar.' },
  'lyrics-fullscreen.png': { role: 'showcase', alt: 'Full-screen synced lyrics with the active line highlighted over an album-tinted background.' },
  'cover-flow.png': { role: 'showcase', alt: 'Cover Flow browsing, with album covers fanned in 3D around a centred album.' },
  /* "compilations" would be wrong — the shipped filter chips are
     All / Albums / Singles / EPs / Other. See src/data/verified-claims.md. */
  'albums-grid.png': { role: 'showcase', alt: 'The album grid with the release-type filter open, narrowing to singles.' },
  'lyrics-panel.png': { role: 'showcase', alt: 'An album page with the lyrics panel open alongside the track list.' },
  'settings-appearance.png': { role: 'card', alt: 'The appearance settings, showing theme choices and the custom accent colour picker.' },
  'settings-eq.png': { role: 'card', alt: 'The parametric equaliser with its frequency response curve and presets.' },
  'songs-hires.png': { role: 'card', alt: 'The tag editor open over the songs list, with every metadata field editable.' },
  'album-detail.png': { role: 'card', alt: 'An album page with cover art, description and track list.' },
  'artist-page.png': { role: 'card', alt: 'An artist page with a header image, biography and discography.' },
  'artists-grid.png': { role: 'card', alt: 'The artists grid with circular artist images.' },
  'queue-popup.png': { role: 'card', alt: 'The play queue popup, showing what is playing next.' },
};

/* Quality raised from 58/80: at those settings the zoomed card crops came out
   visibly soft. 4:4:4 keeps the app UI's thin text edges clean. */
const AVIF = { quality: 70, effort: 6, chromaSubsampling: '4:4:4' };
const WEBP = { quality: 88, effort: 5 };

await mkdir(OUT_DIR, { recursive: true });
await mkdir(ICON_DIR, { recursive: true });
await mkdir(dirname(MANIFEST), { recursive: true });

/**
 * The derived images are committed, so a machine without the raw screenshots
 * (any CI checkout) must not fail the build — it just has nothing to redo.
 * Set SHOTS_DIR to point at the sources on a machine that has them.
 */
const available = new Set(await readdir(SRC_DIR).catch(() => []));
if (!available.size) {
  const generated = (await readdir(OUT_DIR).catch(() => [])).length;
  if (generated) {
    console.log(`no raw screenshots at ${SRC_DIR}`);
    console.log(`${generated} derived files already present — nothing to do.`);
    process.exit(0);
  }
  console.error(`! no screenshots at ${SRC_DIR} and none generated yet.`);
  console.error('  Set SHOTS_DIR to the folder holding the raw .png captures.');
  process.exit(1);
}

const manifest = {};
let totalBytes = 0;

for (const [file, meta] of Object.entries(SHOTS)) {
  if (!available.has(file)) {
    console.warn(`! missing ${file} — skipping`);
    continue;
  }

  const slug = basename(file, extname(file));
  const input = resolve(SRC_DIR, file);
  const src = sharp(input);
  const { width: sw, height: sh } = await src.metadata();
  const aspect = sh / sw;

  const [w1x, w2x] = ROLES[meta.role];
  const variants = {};

  for (const [density, width] of [
    ['1x', w1x],
    ['2x', w2x],
  ]) {
    const w = Math.min(width, sw);
    const h = Math.round(w * aspect);

    for (const [fmt, opts] of [
      ['avif', AVIF],
      ['webp', WEBP],
    ]) {
      const name = `${slug}-${w}.${fmt}`;
      const out = resolve(OUT_DIR, name);
      const info = await sharp(input)
        .resize(w, h, { fit: 'fill', kernel: 'lanczos3' })
        [fmt](opts)
        .toFile(out);
      totalBytes += info.size;
      variants[`${fmt}${density}`] = { src: `/shots/${name}`, width: w, height: h, bytes: info.size };
    }
  }

  manifest[slug] = {
    alt: meta.alt,
    role: meta.role,
    // Intrinsic size for the <img> element = the 1x variant.
    width: variants.avif1x.width,
    height: variants.avif1x.height,
    avif: [variants.avif1x, variants.avif2x],
    webp: [variants.webp1x, variants.webp2x],
  };

  const kb = (n) => `${(n / 1024).toFixed(0)}kB`;
  console.log(
    `${slug.padEnd(22)} ${variants.avif1x.width}x${variants.avif1x.height}  ` +
      `avif ${kb(variants.avif1x.bytes)}/${kb(variants.avif2x.bytes)}  ` +
      `webp ${kb(variants.webp1x.bytes)}/${kb(variants.webp2x.bytes)}`
  );
}

/* --- Favicons ------------------------------------------------------------ */

const iconSizes = [
  ['favicon-32.png', 32],
  ['favicon-48.png', 48],
  ['apple-touch-icon.png', 180],
  ['icon-192.png', 192],
  ['icon-512.png', 512],
];

for (const [name, size] of iconSizes) {
  await sharp(LOGO_SVG, { density: 400 })
    .resize(size, size, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
    .png({ compressionLevel: 9 })
    .toFile(resolve(ICON_DIR, name));
}

// Separate full-bleed art for `purpose: maskable` — the transparent-cornered
// disc would be cropped into a transparent wedge by Android's mask.
await sharp(MASKABLE_SVG, { density: 400 })
  .resize(512, 512)
  .png({ compressionLevel: 9 })
  .toFile(resolve(ICON_DIR, 'icon-maskable-512.png'));

console.log(`icons                  ${iconSizes.map(([, s]) => s).join(', ')}px + maskable 512`);

await writeFile(MANIFEST, JSON.stringify(manifest, null, 2) + '\n');
console.log(`\n${Object.keys(manifest).length} images, ${(totalBytes / 1024 / 1024).toFixed(2)} MB total`);
console.log(`wrote ${MANIFEST}`);
