/**
 * Fetches album artwork for the cover strip from the iTunes Search API and
 * emits the same 280/560 AVIF+WebP pairs the existing covers carry, merging
 * the entries into src/data/covers.json.
 *
 * The derived files are committed, like the screenshot set — run this only to
 * add albums to WANTED or to regenerate from scratch. Existing manifest
 * entries for other slugs are preserved; re-fetched slugs are overwritten.
 */
import sharp from 'sharp';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const OUT_DIR = resolve(here, '../public/covers');
const MANIFEST = resolve(here, '../src/data/covers.json');

/** slug -> what to search for and what to print on the card. */
const WANTED = [
  { slug: 'bad', title: 'Bad', artist: 'Michael Jackson', term: 'Michael Jackson Bad Remastered' },
  { slug: 'thriller', title: 'Thriller', artist: 'Michael Jackson', term: 'Michael Jackson Thriller' },
  { slug: 'carter-v', title: 'Tha Carter V', artist: 'Lil Wayne', term: 'Lil Wayne Tha Carter V' },
  { slug: 'carter-iii', title: 'Tha Carter III', artist: 'Lil Wayne', term: 'Lil Wayne Tha Carter III' },
  { slug: 'death-race-for-love', title: 'Death Race for Love', artist: 'Juice WRLD', term: 'Juice WRLD Death Race for Love' },
  { slug: 'goodbye-good-riddance', title: 'Goodbye & Good Riddance', artist: 'Juice WRLD', term: 'Juice WRLD Goodbye Good Riddance' },
  { slug: 'espresso', title: 'Espresso', artist: 'Sabrina Carpenter', term: 'Sabrina Carpenter Espresso' },
  { slug: 'dangerous-woman', title: 'Dangerous Woman', artist: 'Ariana Grande', term: 'Ariana Grande Dangerous Woman' },
  { slug: 'eternal-sunshine', title: 'eternal sunshine', artist: 'Ariana Grande', term: 'Ariana Grande eternal sunshine' },
  { slug: '714ever', title: '714EVER', artist: 'Yung Pinch', term: 'Yung Pinch 714EVER' },
  { slug: 'un-verano-sin-ti', title: 'Un Verano Sin Ti', artist: 'Bad Bunny', term: 'Bad Bunny Un Verano Sin Ti' },
  { slug: 'take-care', title: 'Take Care', artist: 'Drake', term: 'Drake Take Care' },
  { slug: 'seventeen', title: '17', artist: 'XXXTENTACION', term: 'XXXTENTACION 17' },
  { slug: 'question-mark', title: '?', artist: 'XXXTENTACION', term: 'XXXTENTACION ?' },
  { slug: 'guts', title: 'GUTS', artist: 'Olivia Rodrigo', term: 'Olivia Rodrigo GUTS' },
  { slug: 'revival', title: 'Revival', artist: 'Selena Gomez', term: 'Selena Gomez Revival' },
  { slug: 'secret-of-us', title: 'The Secret of Us', artist: 'Gracie Abrams', term: 'Gracie Abrams The Secret of Us' },
];

const AVIF = { quality: 58, effort: 6, chromaSubsampling: '4:2:0' };
const WEBP = { quality: 80, effort: 5 };
const WIDTHS = [280, 560];

const norm = (s) => (s ?? '').toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();

/** Best match wins: verbatim title, then exact normalised, then contains.
    NO blind fallback — a wrong cover is worse than a build error, so anything
    less than a title match throws with the candidates listed. */
async function findArtwork({ term, title, artist }) {
  const url = `https://itunes.apple.com/search?term=${encodeURIComponent(term)}&entity=album&limit=50&country=US`;
  const res = await fetch(url);
  if (!res.ok) throw new Error(`itunes ${res.status} for "${term}"`);
  const { results } = await res.json();
  const nt = norm(title);
  const na = norm(artist);
  const byArtist = results.filter((r) => norm(r.artistName).includes(na));
  const hit =
    byArtist.find((r) => (r.collectionName ?? '') === title) ??
    (nt ? byArtist.find((r) => norm(r.collectionName) === nt) : undefined) ??
    (nt ? byArtist.find((r) => norm(r.collectionName).includes(nt)) : undefined);
  if (!hit?.artworkUrl100) {
    const got = byArtist.slice(0, 6).map((r) => r.collectionName).join(' | ') || '(none by artist)';
    throw new Error(`no confident match for "${term}" — candidates: ${got}`);
  }
  return { art: hit.artworkUrl100.replace('100x100bb', '600x600bb'), matched: `${hit.collectionName} — ${hit.artistName}` };
}

/** Deezer fallback for releases iTunes does not carry (e.g. 714EVER).
    Tries the field-qualified query first, then a plain one — punctuation
    titles like "?" break the album:"…" filter outright. */
async function findDeezer({ title, artist }) {
  const nt = norm(title);
  const na = norm(artist);
  for (const q of [`artist:"${artist}" album:"${title}"`, `${artist} ${title}`]) {
    const res = await fetch(`https://api.deezer.com/search/album?q=${encodeURIComponent(q)}&limit=50`);
    if (!res.ok) continue;
    const { data } = await res.json();
    const mine = (data ?? []).filter((a) => norm(a.artist?.name).includes(na));
    const hit =
      mine.find((a) => (a.title ?? '') === title) ??
      (nt ? mine.find((a) => norm(a.title) === nt) : undefined) ??
      (nt ? mine.find((a) => norm(a.title).includes(nt)) : undefined);
    if (hit?.cover_xl) return { art: hit.cover_xl, matched: `${hit.title} — ${hit.artist.name} (deezer)` };
  }
  throw new Error(`deezer: no confident match for "${artist} ${title}"`);
}

/** Artwork URLs come straight out of third-party JSON — only fetch over https
    from the CDNs those APIs actually serve, before the bytes reach sharp. */
function assertArtUrl(raw) {
  const u = new URL(raw);
  const ok =
    u.protocol === 'https:' &&
    (u.hostname.endsWith('.mzstatic.com') || u.hostname.endsWith('.dzcdn.net'));
  if (!ok) throw new Error(`unexpected artwork host: ${raw}`);
  return u.href;
}

await mkdir(OUT_DIR, { recursive: true });
const manifest = JSON.parse(await readFile(MANIFEST, 'utf8'));

for (const want of WANTED) {
  const { art, matched } = await findArtwork(want).catch(() => findDeezer(want));
  const buf = Buffer.from(await (await fetch(assertArtUrl(art))).arrayBuffer());

  const entry = { slug: want.slug, title: want.title, artist: want.artist, width: 280, height: 280, avif: [], webp: [] };
  for (const w of WIDTHS) {
    for (const [fmt, opts] of [['avif', AVIF], ['webp', WEBP]]) {
      const name = `${want.slug}-${w}.${fmt}`;
      await sharp(buf).resize(w, w, { fit: 'cover' })[fmt](opts).toFile(resolve(OUT_DIR, name));
      entry[fmt].push({ src: `/covers/${name}`, width: w });
    }
  }

  const at = manifest.findIndex((c) => c.slug === want.slug);
  if (at >= 0) manifest[at] = entry;
  else manifest.push(entry);
  console.log(`${want.slug.padEnd(22)} <- ${matched}`);
}

await writeFile(MANIFEST, JSON.stringify(manifest, null, 2) + '\n');
console.log(`\n${manifest.length} covers in ${MANIFEST}`);
