/**
 * Resolves every published release from the GitHub API at build time, parses
 * its notes into structured sections, and writes src/data/changelog.json.
 *
 * Companion to fetch-release.mjs — same repo, same failure policy, different
 * question. That script answers "what is the current version and where are its
 * assets"; this one answers "what changed, release by release". Kept separate
 * so a parser change here can never break the download buttons.
 *
 * The output lives in src/data rather than public/api because nothing fetches
 * it at runtime: the page is rendered from it at build time and the JSON itself
 * is never served. See scripts/fetch-release.mjs for the counterpart that does
 * need a public endpoint.
 *
 * The same scheduled GitHub Action that refreshes download counts runs this and
 * commits the result, so publishing a release updates the changelog page within
 * about a minute. See .github/workflows/downloads.yml.
 */
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const OUT = resolve(here, '../src/data/changelog.json');

const OWNER = 'heartached';
const REPO = 'Noctis';
const API = `https://api.github.com/repos/${OWNER}/${REPO}`;

const headers = {
  'User-Agent': 'noctis-site-build',
  Accept: 'application/vnd.github+json',
  ...(process.env.GITHUB_TOKEN ? { Authorization: `Bearer ${process.env.GITHUB_TOKEN}` } : {}),
};

async function api(path) {
  const res = await fetch(`${API}${path}`, { headers });
  if (!res.ok) throw new Error(`GET ${path} -> ${res.status} ${res.statusText}`);
  return res.json();
}

/**
 * Release notes become page content, so URLs out of them are held to the same
 * rule as the download links: this repo's own https URLs only.
 */
function assertRepoUrl(raw) {
  const u = new URL(raw);
  const ok = u.protocol === 'https:' && u.hostname === 'github.com' && u.pathname.startsWith(`/${OWNER}/${REPO}/`);
  if (!ok) throw new Error(`unexpected release URL: ${raw}`);
  return u.href;
}

/* --- Note parsing --------------------------------------------------------- */

/**
 * Section headings, normalised to the words the page uses. The release notes
 * are written in the past tense of the action ("Added", "Fixed"); the page
 * labels the category ("New Features", "Bug Fixes").
 *
 * Order here is the order on the page: what is new first, then what got better,
 * then what got fixed. Anything unrecognised keeps its own heading and follows.
 */
const SECTION_MAP = [
  [/^(added|new|new features?|features?)$/i, 'New Features'],
  [/^(improved|improvements?|changed|changes)$/i, 'Improvements'],
  [/^(fixed|fixes|bug ?fixes?)$/i, 'Bug Fixes'],
];

const SECTION_ORDER = ['New Features', 'Improvements', 'Bug Fixes'];

function normaliseHeading(raw) {
  const text = raw.trim().replace(/[:\s]+$/, '');
  for (const [re, label] of SECTION_MAP) if (re.test(text)) return label;
  return text;
}

/**
 * Markdown inline syntax reduced to its text. The page renders these strings as
 * TEXT, never as markup — a release body is remote input, and the site ships a
 * hash-based CSP that any injected element or attribute would break. Anything
 * this misses degrades to a visible stray character, which is the safe way to
 * be wrong.
 */
function plain(md) {
  return md
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1') // images -> alt text
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1') // links -> label
    .replace(/`([^`]+)`/g, '$1')
    .replace(/(\*\*|__)(.*?)\1/g, '$2')
    .replace(/(\*|_)(?=\S)(.*?)(?<=\S)\1/g, '$2')
    .replace(/<[^>]*>/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

/**
 * The notes carry a fixed preamble — Discord badge, download table, macOS
 * quarantine note — and a trailing compare link, none of which belong on the
 * page. "## What's Changed" is where the actual content starts; the compare
 * link, a horizontal rule, or the end of the body is where it stops.
 *
 * Falls back to the whole body when that heading is absent, so a release
 * written in a different shape still contributes its bullets.
 */
function contentSlice(body) {
  const start = body.search(/^#{1,3}\s*What'?s\s+Changed\s*$/im);
  let text = start === -1 ? body : body.slice(start).replace(/^[^\n]*\n/, '');

  const stop = text.search(/^\s*(\*\*Full Changelog\*\*|---\s*$)/im);
  if (stop !== -1) text = text.slice(0, stop);

  return text;
}

/**
 * Releases up to v1.2.2 wrote their notes as one flat bullet list with no
 * headings — but every line opens with the verb ("Added …", "Fixed …"), so the
 * category is stated, just not as a heading. Reading it off the verb gives the
 * older half of the page the same three sections as the newer half instead of
 * one anonymous 80-item list.
 *
 * Only ever applied to a release that supplied NO headings of its own. Where
 * the notes say which section a change belongs to, that wins.
 *
 * Everything that is neither an addition nor a fix falls to Improvements: a
 * changelog line is a change, and "Simplified …" or "Unified …" is a poor fit
 * for the other two. Extending the verb list instead would need a new entry
 * every time a release reached for a new word.
 */
function categorise(text) {
  if (/^(added|new)\b/i.test(text)) return 'New Features';
  if (/^fix(ed)?\b/i.test(text)) return 'Bug Fixes';
  return 'Improvements';
}

function groupByVerb(items) {
  const buckets = new Map();
  for (const text of items) {
    const title = categorise(text);
    if (!buckets.has(title)) buckets.set(title, { title, items: [] });
    buckets.get(title).items.push(text);
  }
  return [...buckets.values()];
}

/**
 * Body -> [{ title, items }]. Bullets before any heading collect into an
 * untitled section, which is how a release that just lists its changes without
 * categorising them still renders.
 */
function parseSections(body) {
  if (!body) return [];

  const sections = [];
  let current = null;

  for (const line of contentSlice(body).split(/\r?\n/)) {
    const heading = line.match(/^#{2,4}\s+(.+?)\s*$/);
    if (heading) {
      current = { title: normaliseHeading(heading[1]), items: [] };
      sections.push(current);
      continue;
    }

    // Top-level bullets only. Indented ones are sub-points of the line above;
    // pulling them up to the same level would misrepresent them as peers.
    const bullet = line.match(/^[-*+]\s+(.+?)\s*$/);
    if (!bullet) continue;

    const text = plain(bullet[1]);
    if (!text) continue;

    if (!current) {
      current = { title: null, items: [] };
      sections.push(current);
    }
    current.items.push(text);
  }

  const found = sections.filter((s) => s.items.length);

  const headed = found.some((s) => s.title !== null);
  const resolved = headed ? found : groupByVerb(found.flatMap((s) => s.items));

  const ranked = (s) => {
    const i = SECTION_ORDER.indexOf(s.title);
    return i === -1 ? SECTION_ORDER.length : i;
  };

  return resolved.sort((a, b) => ranked(a) - ranked(b));
}

/* --- Build ---------------------------------------------------------------- */

let payload;

try {
  const all = [];
  for (let page = 1; page <= 10; page++) {
    const batch = await api(`/releases?per_page=100&page=${page}`);
    all.push(...batch);
    if (batch.length < 100) break;
  }

  /**
   * Every published release, newest first — pre-releases included, flagged so
   * the page can badge them the way GitHub does. Only drafts are dropped:
   * they are unpublished and have no place in the product's history.
   *
   * Sorted by published_at, not list order: /releases comes back by created_at,
   * which puts a long-drafted release in the wrong place.
   */
  const releases = all
    .filter((r) => !r.draft && r.published_at)
    .sort((a, b) => new Date(b.published_at) - new Date(a.published_at))
    .map((r) => ({
      // Tag names flow into HTML and into element ids — plain version tokens only.
      tag: r.tag_name.replace(/[^\w.\-+]/g, ''),
      version: r.tag_name.replace(/^v/, '').replace(/[^\w.\-+]/g, ''),
      name: plain(r.name ?? '') || null,
      publishedAt: r.published_at,
      url: assertRepoUrl(r.html_url),
      prerelease: Boolean(r.prerelease),
      sections: parseSections(r.body),
    }));

  if (!releases.length) throw new Error('no published release found');

  payload = { releases, updatedAt: new Date().toISOString(), degraded: false };
} catch (err) {
  console.warn(`! changelog fetch failed: ${err.message}`);
  // Keep the last good file rather than shipping an empty history.
  try {
    const prev = JSON.parse(await readFile(OUT, 'utf8'));
    console.warn('  reusing the previous changelog.json');
    payload = { ...prev, degraded: true };
  } catch {
    console.warn('  no previous data — emitting an empty changelog');
    payload = { releases: [], updatedAt: new Date().toISOString(), degraded: true };
  }
}

/**
 * Keep the previous `updatedAt` when nothing else changed, so the scheduled
 * workflow produces a byte-identical file and git has nothing to commit.
 */
try {
  const prev = JSON.parse(await readFile(OUT, 'utf8'));
  const strip = ({ updatedAt, ...rest }) => JSON.stringify(rest);
  if (strip(prev) === strip(payload)) {
    payload.updatedAt = prev.updatedAt;
    console.log('no change since last run');
  }
} catch {
  /* first run, or unreadable — write fresh */
}

await mkdir(dirname(OUT), { recursive: true });
await writeFile(OUT, JSON.stringify(payload, null, 2) + '\n');

const entries = payload.releases ?? [];
const bullets = entries.reduce((n, r) => n + r.sections.reduce((m, s) => m + s.items.length, 0), 0);
console.log(`releases    : ${entries.length}`);
console.log(`changes     : ${bullets}`);
if (entries.length) {
  const empty = entries.filter((r) => !r.sections.length).map((r) => r.tag);
  console.log(`newest      : ${entries[0].tag}`);
  if (empty.length) console.log(`no notes    : ${empty.join(' ')}`);
}
console.log(`wrote ${OUT}`);
